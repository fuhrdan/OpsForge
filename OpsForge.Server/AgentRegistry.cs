using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OpsForge.Contracts;

namespace OpsForge.Server;

public sealed class AgentRegistry
{
    private static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(20);
    private static readonly Regex ValidAgentId = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly SqliteRepository _repository;

    public AgentRegistry(SqliteRepository repository)
    {
        _repository = repository;
    }

    public (AgentEnrollmentResponse? Response, string? Error) Enroll(AgentEnrollmentRequest request, string? connectionCertificateThumbprint = null)
    {
        request.AgentId = request.AgentId.Trim();
        if (!ValidAgentId.IsMatch(request.AgentId))
        {
            return (null, "AgentId must be 1-64 characters using letters, numbers, dot, underscore, or hyphen.");
        }
        if (_repository.AgentExists(request.AgentId))
        {
            return (null, "That AgentId is already enrolled. Rotate its credential or revoke it instead of enrolling a duplicate.");
        }

        var now = DateTimeOffset.UtcNow;
        var apiKey = SecuritySecrets.GenerateAgentApiKey();
        var hash = SecuritySecrets.HashApiKey(apiKey);
        var fingerprint = SecuritySecrets.FingerprintFromHash(hash);
        var certificateThumbprint = NormalizeThumbprint(connectionCertificateThumbprint ?? request.ClientCertificateThumbprint);
        _repository.EnrollAgent(request, hash, fingerprint, certificateThumbprint, now);
        return (new AgentEnrollmentResponse
        {
            AgentId = request.AgentId,
            ApiKey = apiKey,
            CredentialFingerprint = fingerprint,
            EnrolledUtc = now,
            Note = "This API key is returned once. Store it on the agent and do not commit it to source control.",
            ClientCertificateThumbprint = certificateThumbprint
        }, null);
    }

    public bool Authenticate(string agentId, string? apiKey, string? clientCertificateThumbprint, bool requireClientCertificate)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return false;
        var record = _repository.GetAgentCredential(agentId);
        if (record is null || record.Value.Revoked) return false;
        var suppliedHash = SecuritySecrets.HashApiKey(apiKey.Trim());
        if (!FixedHashEquals(record.Value.Hash, suppliedHash)) return false;
        if (!requireClientCertificate) return true;
        var expected = NormalizeThumbprint(record.Value.ClientCertificateThumbprint);
        var supplied = NormalizeThumbprint(clientCertificateThumbprint);
        return !string.IsNullOrWhiteSpace(expected) && FixedHashEquals(expected, supplied);
    }

    public (bool Success, string? Error) BindCertificate(string agentId, string thumbprint)
    {
        var agent = _repository.GetAgentInventory(agentId);
        if (agent is null) return (false, "Agent not found.");
        if (agent.Revoked) return (false, "Revoked agents cannot bind certificates.");
        thumbprint = NormalizeThumbprint(thumbprint);
        if (string.IsNullOrWhiteSpace(thumbprint)) return (false, "Certificate thumbprint is required.");
        _repository.BindAgentCertificate(agentId, thumbprint, DateTimeOffset.UtcNow);
        return (true, null);
    }

    public void RecordHeartbeat(AgentHeartbeatRequest heartbeat, string remoteIpAddress) =>
        _repository.RecordAgentHeartbeat(heartbeat, remoteIpAddress, DateTimeOffset.UtcNow);

    public IReadOnlyList<AgentInventoryDto> GetInventory()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var agent in _repository.GetAgentInventory())
        {
            if (!agent.Revoked && agent.LastSeenUtc.HasValue &&
                now - agent.LastSeenUtc.Value > OfflineAfter &&
                string.Equals(agent.Status, "online", StringComparison.OrdinalIgnoreCase))
            {
                _repository.MarkAgentOffline(agent.AgentId, now,
                    $"No authenticated heartbeat received for more than {OfflineAfter.TotalSeconds:F0} seconds.");
            }
        }
        return _repository.GetAgentInventory();
    }

    public IReadOnlyList<AgentStatusHistoryDto> GetHistory(string agentId) =>
        _repository.GetAgentStatusHistory(agentId);

    public (AgentEnrollmentResponse? Response, string? Error) RotateCredential(string agentId)
    {
        var current = _repository.GetAgentInventory(agentId);
        if (current is null)
        {
            return (null, "Agent not found.");
        }
        if (current.Revoked)
        {
            return (null, "Revoked agents cannot rotate credentials. Re-enroll with a new AgentId.");
        }

        var apiKey = SecuritySecrets.GenerateAgentApiKey();
        var hash = SecuritySecrets.HashApiKey(apiKey);
        var fingerprint = SecuritySecrets.FingerprintFromHash(hash);
        var now = DateTimeOffset.UtcNow;
        _repository.RotateAgentCredential(agentId, hash, fingerprint, now);
        return (new AgentEnrollmentResponse
        {
            AgentId = agentId,
            ApiKey = apiKey,
            CredentialFingerprint = fingerprint,
            EnrolledUtc = current.EnrolledUtc,
            Note = "Credential rotated. The previous key stopped working immediately; update the agent before its next request.",
            ClientCertificateThumbprint = current.ClientCertificateThumbprint
        }, null);
    }

    public bool Revoke(string agentId)
    {
        if (_repository.GetAgentInventory(agentId) is null)
        {
            return false;
        }
        _repository.RevokeAgent(agentId, DateTimeOffset.UtcNow);
        return true;
    }

    private static string NormalizeThumbprint(string? value) =>
        (value ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private static bool FixedHashEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

}
