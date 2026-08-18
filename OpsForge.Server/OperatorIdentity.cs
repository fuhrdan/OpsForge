using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OpsForge.Contracts;

namespace OpsForge.Server;

public static class OpsForgeRoles
{
    public const string Viewer = "viewer";
    public const string Operator = "operator";
    public const string Administrator = "administrator";

    public static bool IsValid(string? role) => role is not null &&
        (string.Equals(role, Viewer, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(role, Operator, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(role, Administrator, StringComparison.OrdinalIgnoreCase));

    public static bool AtLeast(string actual, string required)
    {
        static int Rank(string role) => role.ToLowerInvariant() switch
        {
            Administrator => 3,
            Operator => 2,
            Viewer => 1,
            _ => 0
        };
        return Rank(actual) >= Rank(required);
    }
}

public sealed class OperatorIdentity
{
    public const string SessionCookieName = "opsforge_session";
    private const int PasswordIterations = 210_000;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);
    private static readonly Regex UsernamePattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{2,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly SqliteRepository _repository;
    private readonly SecuritySecrets _secrets;
    private readonly ConcurrentDictionary<string, FailedLoginWindow> _failedLogins = new(StringComparer.OrdinalIgnoreCase);

    public OperatorIdentity(SqliteRepository repository, SecuritySecrets secrets)
    {
        _repository = repository;
        _secrets = secrets;
        EnsureBootstrapAdministrator();
    }

    public (AuthPrincipal? Principal, string? SessionToken, string? CsrfToken, string? Error) Login(string username, string password, string remoteIp)
    {
        username = (username ?? string.Empty).Trim();
        var limiterKey = $"{username}|{remoteIp}";
        if (IsRateLimited(limiterKey)) return (null, null, null, "Too many failed login attempts. Try again in several minutes.");

        var record = _repository.GetOperatorUserAuth(username);
        if (record is null || !record.Enabled || !VerifyPassword(password, record.PasswordSalt, record.PasswordHash, record.PasswordIterations))
        {
            RegisterFailure(limiterKey);
            _repository.AddAuditEvent(username, username, "unknown", "auth.login", "session", "Login rejected.", "denied", remoteIp, DateTimeOffset.UtcNow);
            return (null, null, null, "Invalid username or password.");
        }

        _failedLogins.TryRemove(limiterKey, out _);
        var sessionToken = SecuritySecrets.GenerateSessionToken();
        var csrfToken = SecuritySecrets.GenerateCsrfToken();
        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(SessionLifetime);
        _repository.CreateOperatorSession(record.UserId, SecuritySecrets.HashSecret(sessionToken), SecuritySecrets.HashSecret(csrfToken), now, expires, remoteIp);
        _repository.UpdateOperatorLastLogin(record.UserId, now);
        var principal = new AuthPrincipal(record.UserId, record.Username, record.DisplayName, record.Role, record.Enabled, record.MustChangePassword, expires, SecuritySecrets.HashSecret(sessionToken));
        _repository.AddAuditEvent(record.Username, record.DisplayName, record.Role, "auth.login", "session", "Interactive login succeeded.", "success", remoteIp, now);
        return (principal, sessionToken, csrfToken, null);
    }

    public AuthPrincipal? Authenticate(string? sessionToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken)) return null;
        return _repository.GetOperatorSession(SecuritySecrets.HashSecret(sessionToken.Trim()), DateTimeOffset.UtcNow);
    }

    public bool ValidateCsrf(AuthPrincipal principal, string? csrfToken)
    {
        if (string.IsNullOrWhiteSpace(csrfToken)) return false;
        return _repository.ValidateSessionCsrf(principal.SessionTokenHash, SecuritySecrets.HashSecret(csrfToken.Trim()), DateTimeOffset.UtcNow);
    }

    public void Logout(AuthPrincipal principal, string remoteIp)
    {
        _repository.DeleteOperatorSession(principal.SessionTokenHash);
        _repository.AddAuditEvent(principal.Username, principal.DisplayName, principal.Role, "auth.logout", "session", "Interactive session ended.", "success", remoteIp, DateTimeOffset.UtcNow);
    }

    public (bool Success, string? Error) ChangePassword(AuthPrincipal principal, string currentPassword, string newPassword, string remoteIp)
    {
        var record = _repository.GetOperatorUserAuth(principal.Username);
        if (record is null || !VerifyPassword(currentPassword, record.PasswordSalt, record.PasswordHash, record.PasswordIterations))
            return (false, "Current password is incorrect.");
        var policy = ValidatePassword(newPassword);
        if (policy is not null) return (false, policy);
        var (salt, hash) = HashPassword(newPassword);
        var now = DateTimeOffset.UtcNow;
        _repository.SetOperatorPassword(record.UserId, salt, hash, PasswordIterations, false, now);
        _repository.DeleteOtherSessionsForUser(record.UserId, principal.SessionTokenHash);
        if (string.Equals(principal.Username, "admin", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(_secrets.BootstrapAdminPath)) File.Delete(_secrets.BootstrapAdminPath);
            }
            catch
            {
                // Password rotation succeeded even if the obsolete bootstrap file could not be removed.
            }
        }
        _repository.AddAuditEvent(principal.Username, principal.DisplayName, principal.Role, "user.password.change", principal.Username, "Password changed; other sessions revoked.", "success", remoteIp, now);
        return (true, null);
    }

    public (OperatorUserDto? User, string? TemporaryPassword, string? Error) CreateUser(CreateOperatorUserRequest request, AuthPrincipal actor, string remoteIp)
    {
        var username = (request.Username ?? string.Empty).Trim();
        var displayName = (request.DisplayName ?? string.Empty).Trim();
        var role = (request.Role ?? string.Empty).Trim().ToLowerInvariant();
        if (!UsernamePattern.IsMatch(username)) return (null, null, "Username must be 3-64 characters using letters, numbers, dot, underscore, or hyphen.");
        if (!OpsForgeRoles.IsValid(role)) return (null, null, "Role must be viewer, operator, or administrator.");
        if (_repository.GetOperatorUserAuth(username) is not null) return (null, null, "That username already exists.");
        var temp = SecuritySecrets.GenerateTemporaryPassword();
        var (salt, hash) = HashPassword(temp);
        var now = DateTimeOffset.UtcNow;
        var user = _repository.CreateOperatorUser(username, string.IsNullOrWhiteSpace(displayName) ? username : displayName, role, salt, hash, PasswordIterations, true, now);
        _repository.AddAuditEvent(actor.Username, actor.DisplayName, actor.Role, "user.create", username, $"Created {role} account with forced password change.", "success", remoteIp, now);
        return (user, temp, null);
    }

    public (string? TemporaryPassword, string? Error) ResetPassword(long userId, AuthPrincipal actor, string remoteIp)
    {
        var user = _repository.GetOperatorUser(userId);
        if (user is null) return (null, "User not found.");
        var temp = SecuritySecrets.GenerateTemporaryPassword();
        var (salt, hash) = HashPassword(temp);
        var now = DateTimeOffset.UtcNow;
        _repository.SetOperatorPassword(userId, salt, hash, PasswordIterations, true, now);
        _repository.DeleteSessionsForUser(userId);
        _repository.AddAuditEvent(actor.Username, actor.DisplayName, actor.Role, "user.password.reset", user.Username, "Administrator reset password; all sessions revoked and password change required.", "success", remoteIp, now);
        return (temp, null);
    }

    public (bool Success, string? Error) SetEnabled(long userId, bool enabled, AuthPrincipal actor, string remoteIp)
    {
        var user = _repository.GetOperatorUser(userId);
        if (user is null) return (false, "User not found.");
        if (!enabled && user.UserId == actor.UserId) return (false, "You cannot disable your own active account.");
        _repository.SetOperatorUserEnabled(userId, enabled);
        if (!enabled) _repository.DeleteSessionsForUser(userId);
        _repository.AddAuditEvent(actor.Username, actor.DisplayName, actor.Role, enabled ? "user.enable" : "user.disable", user.Username, enabled ? "Account enabled." : "Account disabled and sessions revoked.", "success", remoteIp, DateTimeOffset.UtcNow);
        return (true, null);
    }

    public IReadOnlyList<OperatorUserDto> GetUsers() => _repository.GetOperatorUsers();

    public AuthSessionDto ToSessionDto(AuthPrincipal principal, string csrfToken) => new()
    {
        Authenticated = true,
        User = _repository.GetOperatorUser(principal.UserId),
        CsrfToken = csrfToken,
        ExpiresUtc = principal.ExpiresUtc
    };

    public static string? ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 12) return "Password must be at least 12 characters.";
        if (!password.Any(char.IsUpper) || !password.Any(char.IsLower) || !password.Any(char.IsDigit) || !password.Any(c => !char.IsLetterOrDigit(c)))
            return "Password must include uppercase, lowercase, a number, and a symbol.";
        return null;
    }

    private void EnsureBootstrapAdministrator()
    {
        if (_repository.CountOperatorUsers() > 0) return;
        var password = SecuritySecrets.GenerateTemporaryPassword();
        var (salt, hash) = HashPassword(password);
        var now = DateTimeOffset.UtcNow;
        _repository.CreateOperatorUser("admin", "OpsForge Administrator", OpsForgeRoles.Administrator, salt, hash, PasswordIterations, true, now);
        Directory.CreateDirectory(Path.GetDirectoryName(_secrets.BootstrapAdminPath)!);
        File.WriteAllText(_secrets.BootstrapAdminPath,
            $"OpsForge v0.7.2 bootstrap administrator{Environment.NewLine}" +
            $"Username: admin{Environment.NewLine}" +
            $"Temporary password: {password}{Environment.NewLine}" +
            $"You must change this password after first login.{Environment.NewLine}");
        _repository.AddAuditEvent("system", "OpsForge", "system", "user.bootstrap", "admin", "Created initial administrator account. Credentials written to the local bootstrap file.", "success", "local", now);
    }

    private static (string Salt, string Hash) HashPassword(string password)
    {
        var saltBytes = RandomNumberGenerator.GetBytes(16);
        var hashBytes = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), saltBytes, PasswordIterations, HashAlgorithmName.SHA256, 32);
        return (Convert.ToBase64String(saltBytes), Convert.ToBase64String(hashBytes));
    }

    private static bool VerifyPassword(string password, string salt, string expectedHash, int iterations)
    {
        try
        {
            var saltBytes = Convert.FromBase64String(salt);
            var expected = Convert.FromBase64String(expectedHash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password ?? string.Empty), saltBytes, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }

    private bool IsRateLimited(string key)
    {
        if (!_failedLogins.TryGetValue(key, out var value)) return false;
        if (DateTimeOffset.UtcNow - value.WindowStart > TimeSpan.FromMinutes(15)) { _failedLogins.TryRemove(key, out _); return false; }
        return value.Count >= 5;
    }

    private void RegisterFailure(string key)
    {
        var now = DateTimeOffset.UtcNow;
        _failedLogins.AddOrUpdate(key,
            _ => new FailedLoginWindow(now, 1),
            (_, old) => now - old.WindowStart > TimeSpan.FromMinutes(15) ? new FailedLoginWindow(now, 1) : old with { Count = old.Count + 1 });
    }

    private sealed record FailedLoginWindow(DateTimeOffset WindowStart, int Count);
}

public sealed record AuthPrincipal(long UserId, string Username, string DisplayName, string Role, bool Enabled, bool MustChangePassword, DateTimeOffset ExpiresUtc, string SessionTokenHash);
public sealed record OperatorUserAuthRecord(long UserId, string Username, string DisplayName, string Role, bool Enabled, bool MustChangePassword, string PasswordSalt, string PasswordHash, int PasswordIterations, DateTimeOffset CreatedUtc, DateTimeOffset? LastLoginUtc, DateTimeOffset? PasswordChangedUtc);
