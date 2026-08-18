using System.Security.Cryptography;
using System.Text;
using OpsForge.Contracts;

namespace OpsForge.Server;

public sealed class SecuritySecrets
{
    private readonly string _enrollmentToken;
    private readonly bool _enrollmentFromEnvironment;

    public SecuritySecrets(IWebHostEnvironment environment)
    {
        var root = Environment.GetEnvironmentVariable("OPSFORGE_ROOT");
        var dataDirectory = !string.IsNullOrWhiteSpace(root)
            ? Path.Combine(root, "data", "security")
            : Path.Combine(environment.ContentRootPath, "data", "security");
        Directory.CreateDirectory(dataDirectory);

        EnrollmentTokenPath = Path.Combine(dataDirectory, "enrollment-token.txt");
        BootstrapAdminPath = Path.Combine(dataDirectory, "admin-bootstrap.txt");
        _enrollmentFromEnvironment = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPSFORGE_ENROLLMENT_TOKEN"));
        _enrollmentToken = GetOrCreateSecret("OPSFORGE_ENROLLMENT_TOKEN", EnrollmentTokenPath, "ofe_");
    }

    public string EnrollmentTokenPath { get; }
    public string BootstrapAdminPath { get; }
    public bool AgentMtlsEnabled => string.Equals(Environment.GetEnvironmentVariable("OPSFORGE_AGENT_MTLS"), "1", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(Environment.GetEnvironmentVariable("OPSFORGE_AGENT_MTLS"), "true", StringComparison.OrdinalIgnoreCase);

    public bool ValidateEnrollmentToken(string? candidate) => FixedEquals(_enrollmentToken, candidate);

    public static string GenerateAgentApiKey() => "ofa_" + Base64Url(RandomNumberGenerator.GetBytes(32));
    public static string GenerateSessionToken() => "ofs_" + Base64Url(RandomNumberGenerator.GetBytes(32));
    public static string GenerateCsrfToken() => "ofc_" + Base64Url(RandomNumberGenerator.GetBytes(24));
    public static string GenerateTemporaryPassword() => "OF!" + Base64Url(RandomNumberGenerator.GetBytes(18));

    public static string HashSecret(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash);
    }

    public static string HashApiKey(string apiKey) => HashSecret(apiKey);

    public static string FingerprintFromHash(string hash) => hash.Length <= 12 ? hash : hash[..12];

    public SecurityStatusDto GetStatus(bool isHttps) => new()
    {
        AgentAuthenticationRequired = true,
        EnrollmentTokenRequired = true,
        UserAuthenticationRequired = true,
        RoleBasedAccessControl = true,
        AgentMtlsEnabled = AgentMtlsEnabled,
        Https = isHttps,
        TransportGuidance = isHttps
            ? (AgentMtlsEnabled ? "HTTPS is active. Agent endpoints require API credentials plus a bound client certificate." : "HTTPS is active. Agent API credentials are protected in transit; optional mTLS can be enabled with OPSFORGE_AGENT_MTLS=1.")
            : "HTTP is suitable only for loopback. Use HTTPS for multi-machine deployments; remote agents refuse insecure HTTP by default.",
        EnrollmentTokenLocation = _enrollmentFromEnvironment ? "OPSFORGE_ENROLLMENT_TOKEN environment variable" : RelativeSecretPath(EnrollmentTokenPath),
        BootstrapAdminLocation = File.Exists(BootstrapAdminPath) ? RelativeSecretPath(BootstrapAdminPath) : "consumed / no bootstrap credential file present",
        Roles = new[] { "viewer", "operator", "administrator" }
    };

    private static string GetOrCreateSecret(string environmentName, string path, string prefix)
    {
        var configured = Environment.GetEnvironmentVariable(environmentName);
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (!string.IsNullOrWhiteSpace(existing)) return existing;
        }
        var created = prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(path, created + Environment.NewLine);
        return created;
    }

    public static bool FixedEquals(string expected, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        var left = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var right = SHA256.HashData(Encoding.UTF8.GetBytes(candidate.Trim()));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string RelativeSecretPath(string path)
    {
        var root = Environment.GetEnvironmentVariable("OPSFORGE_ROOT");
        if (string.IsNullOrWhiteSpace(root)) return path;
        return Path.GetRelativePath(root, path).Replace('\\', '/');
    }
}
