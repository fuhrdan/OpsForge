using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using OpsForge.Contracts;

namespace OpsForge.Agent;

internal static class Program
{
    private const string Version = "0.7.2";

    public static async Task Main()
    {
        Console.Title = "OpsForge Agent v0.7.2";
        Console.WriteLine("OpsForge Agent v0.7.2");
        Console.WriteLine("Authenticated telemetry, optional mTLS identity, Windows services, HTTP/TCP/DNS probes, and constrained commands.");
        Console.WriteLine();

        var options = AgentOptions.Load();
        var agentId = Environment.GetEnvironmentVariable("OPSFORGE_AGENT_ID");
        if (string.IsNullOrWhiteSpace(agentId))
            agentId = string.IsNullOrWhiteSpace(options.AgentId) ? $"{Environment.MachineName.ToLowerInvariant()}-01" : options.AgentId;

        var displayName = Environment.GetEnvironmentVariable("OPSFORGE_AGENT_NAME") ?? options.DisplayName;
        var site = Environment.GetEnvironmentVariable("OPSFORGE_AGENT_SITE") ?? options.Site;
        var environmentName = Environment.GetEnvironmentVariable("OPSFORGE_AGENT_ENVIRONMENT") ?? options.EnvironmentName;
        var serverUri = new Uri(options.ServerUrl.TrimEnd('/') + "/");
        ValidateTransport(serverUri, options.AllowInsecureRemoteHttp);

        using var certificateMaterial = options.UseClientCertificate ? EnsureClientCertificate(agentId, options) : null;
        using var handler = new HttpClientHandler();
        if (certificateMaterial is not null)
            handler.ClientCertificates.Add(certificateMaterial.Certificate);

        using var collector = new SystemMetricsCollector(options.MonitoredProcesses, options.MonitoredServices, options.HealthChecks);
        using var http = new HttpClient(handler) { BaseAddress = serverUri, Timeout = TimeSpan.FromSeconds(8) };

        var apiKey = LoadApiKey(agentId, options);
        if (string.IsNullOrWhiteSpace(apiKey))
            apiKey = await EnrollAsync(http, agentId, displayName, site, environmentName, options, certificateMaterial);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("No agent API key is available. Set OPSFORGE_AGENT_ENROLLMENT_TOKEN for first enrollment or OPSFORGE_AGENT_API_KEY for an existing enrollment.");
            Console.ResetColor();
            return;
        }

        http.DefaultRequestHeaders.Add("X-OpsForge-Agent-Key", apiKey);
        Console.WriteLine($"Credential loaded for {agentId}. API key is not printed.");
        if (certificateMaterial is not null) Console.WriteLine($"Client certificate: {certificateMaterial.Certificate.Thumbprint} (self-managed agent identity)");
        if (serverUri.Scheme == Uri.UriSchemeHttps) Console.WriteLine("Transport: HTTPS with normal server-certificate validation.");
        Console.WriteLine();

        collector.PrimeCpu();
        await Task.Delay(600);
        while (true)
        {
            try
            {
                var heartbeat = await collector.CollectAsync(agentId, Version, displayName, site, environmentName);
                using var response = await http.PostAsJsonAsync("api/agents/heartbeat", heartbeat);
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    throw new InvalidOperationException("Server rejected the agent credential and/or bound client certificate. Rotate/re-enroll the credential or verify the mTLS certificate binding.");
                response.EnsureSuccessStatusCode();
                var probesHealthy = heartbeat.Probes.Count(p => p.Success);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] authenticated heartbeat · CPU {heartbeat.CpuPercent:F1}% · RAM {heartbeat.MemoryUsedPercent:F1}% · probes {probesHealthy}/{heartbeat.Probes.Count}");
                await PollAndExecuteCommand(http, agentId, options);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {ex.GetType().Name}: {ex.Message}");
                Console.ResetColor();
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.HeartbeatSeconds)));
        }
    }

    private static async Task<string?> EnrollAsync(HttpClient http, string agentId, string displayName, string site,
        string environmentName, AgentOptions options, ClientCertificateMaterial? certificateMaterial)
    {
        var token = Environment.GetEnvironmentVariable("OPSFORGE_AGENT_ENROLLMENT_TOKEN") ?? options.EnrollmentToken;
        if (string.IsNullOrWhiteSpace(token)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/enrollment/agents");
        request.Headers.Add("X-OpsForge-Enrollment-Token", token);
        request.Content = JsonContent.Create(new AgentEnrollmentRequest
        {
            AgentId = agentId,
            DisplayName = displayName,
            Site = site,
            EnvironmentName = environmentName,
            ClientCertificateThumbprint = certificateMaterial?.Certificate.Thumbprint ?? string.Empty
        });
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Enrollment failed ({(int)response.StatusCode}): {detail}");
        }
        var enrollment = await response.Content.ReadFromJsonAsync<AgentEnrollmentResponse>();
        if (enrollment is null || string.IsNullOrWhiteSpace(enrollment.ApiKey)) throw new InvalidOperationException("Enrollment response did not include an API key.");
        SaveCredential(agentId, enrollment.ApiKey, enrollment.CredentialFingerprint, options, certificateMaterial);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Agent enrolled. Credential fingerprint {enrollment.CredentialFingerprint}; key stored in {ResolveCredentialPath(agentId, options)}.");
        Console.ResetColor();
        return enrollment.ApiKey;
    }

    private static string? LoadApiKey(string agentId, AgentOptions options)
    {
        var environmentKey = Environment.GetEnvironmentVariable("OPSFORGE_AGENT_API_KEY");
        if (!string.IsNullOrWhiteSpace(environmentKey)) return environmentKey.Trim();
        if (!string.IsNullOrWhiteSpace(options.ApiKey)) return options.ApiKey.Trim();
        var credential = LoadCredentialFile(agentId, options);
        return credential?.ApiKey;
    }

    private static AgentCredentialFile? LoadCredentialFile(string agentId, AgentOptions options)
    {
        var path = ResolveCredentialPath(agentId, options);
        if (!File.Exists(path)) return null;
        try
        {
            var credential = JsonSerializer.Deserialize<AgentCredentialFile>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return credential is not null && string.Equals(credential.AgentId, agentId, StringComparison.OrdinalIgnoreCase) ? credential : null;
        }
        catch { return null; }
    }

    private static void SaveCredential(string agentId, string apiKey, string fingerprint, AgentOptions options, ClientCertificateMaterial? certificateMaterial)
    {
        var path = ResolveCredentialPath(agentId, options);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var existing = LoadCredentialFile(agentId, options) ?? new AgentCredentialFile { AgentId = agentId };
        existing.ApiKey = apiKey;
        existing.CredentialFingerprint = fingerprint;
        existing.SavedUtc = DateTimeOffset.UtcNow;
        if (certificateMaterial is not null)
        {
            existing.ClientCertificatePath = certificateMaterial.Path;
            existing.ClientCertificatePassword = certificateMaterial.Password;
            existing.ClientCertificateThumbprint = certificateMaterial.Certificate.Thumbprint ?? string.Empty;
        }
        File.WriteAllText(path, JsonSerializer.Serialize(existing, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static ClientCertificateMaterial? EnsureClientCertificate(string agentId, AgentOptions options)
    {
        var envPath = Environment.GetEnvironmentVariable("OPSFORGE_AGENT_CLIENT_CERT_PFX");
        var envPassword = Environment.GetEnvironmentVariable("OPSFORGE_AGENT_CLIENT_CERT_PASSWORD");
        if (!string.IsNullOrWhiteSpace(envPath))
            return LoadCertificate(Path.GetFullPath(envPath), envPassword ?? options.ClientCertificatePassword);

        if (!string.IsNullOrWhiteSpace(options.ClientCertificatePfx))
            return LoadCertificate(Path.GetFullPath(options.ClientCertificatePfx), options.ClientCertificatePassword);

        var existing = LoadCredentialFile(agentId, options);
        if (existing is not null && !string.IsNullOrWhiteSpace(existing.ClientCertificatePath) && File.Exists(existing.ClientCertificatePath))
            return LoadCertificate(existing.ClientCertificatePath, existing.ClientCertificatePassword);

        var credentialPath = ResolveCredentialPath(agentId, options);
        var directory = Path.GetDirectoryName(credentialPath)!;
        Directory.CreateDirectory(directory);
        var pfxPath = Path.Combine(directory, $"{agentId}.client.pfx");
        var password = "ofp_" + Base64Url(RandomNumberGenerator.GetBytes(24));

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(new X500DistinguishedName($"CN=OpsForge Agent {agentId}"), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") }, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2));
        File.WriteAllBytes(pfxPath, created.Export(X509ContentType.Pfx, password));

        var credential = existing ?? new AgentCredentialFile { AgentId = agentId };
        credential.ClientCertificatePath = pfxPath;
        credential.ClientCertificatePassword = password;
        credential.ClientCertificateThumbprint = created.Thumbprint ?? string.Empty;
        credential.SavedUtc = DateTimeOffset.UtcNow;
        File.WriteAllText(credentialPath, JsonSerializer.Serialize(credential, new JsonSerializerOptions { WriteIndented = true }));
        return LoadCertificate(pfxPath, password);
    }

    private static ClientCertificateMaterial LoadCertificate(string path, string password)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Agent client certificate PFX was not found.", path);
        var cert = new X509Certificate2(path, password, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        if (!cert.HasPrivateKey) { cert.Dispose(); throw new InvalidOperationException("Agent client certificate does not contain a private key."); }
        return new ClientCertificateMaterial(path, password, cert);
    }

    private static string ResolveCredentialPath(string agentId, AgentOptions options)
    {
        var configured = Environment.GetEnvironmentVariable("OPSFORGE_AGENT_CREDENTIALS");
        if (string.IsNullOrWhiteSpace(configured)) configured = options.CredentialsFile;
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var baseDirectory = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPSFORGE_ROOT"))
            ? Path.Combine(Environment.GetEnvironmentVariable("OPSFORGE_ROOT")!, "data", "agents")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpsForge");
        return Path.Combine(baseDirectory, $"{agentId}.credentials.json");
    }

    private static void ValidateTransport(Uri serverUri, bool allowInsecureRemoteHttp)
    {
        var loopback = serverUri.IsLoopback || string.Equals(serverUri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        if (serverUri.Scheme != Uri.UriSchemeHttps && !loopback && !allowInsecureRemoteHttp)
            throw new InvalidOperationException("Remote OpsForge servers must use HTTPS. Configure TLS on the server/reverse proxy, or explicitly set allowInsecureRemoteHttp=true only for a trusted disposable lab.");
    }

    private static async Task PollAndExecuteCommand(HttpClient http, string agentId, AgentOptions options)
    {
        using var response = await http.GetAsync($"api/agents/{Uri.EscapeDataString(agentId)}/commands/next");
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return;
        response.EnsureSuccessStatusCode();
        var command = await response.Content.ReadFromJsonAsync<AgentCommandDto>();
        if (command is null) return;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] authenticated command · {command.Type} {command.Target}");
        Console.ResetColor();
        var result = ExecuteCommand(command, options);
        using var resultResponse = await http.PostAsJsonAsync($"api/agents/{Uri.EscapeDataString(agentId)}/commands/{command.CommandId}/result", result);
        resultResponse.EnsureSuccessStatusCode();
    }

    private static CommandResultRequest ExecuteCommand(AgentCommandDto command, AgentOptions options)
    {
        try
        {
            var allowed = options.MonitoredProcesses.Any(name => string.Equals(name, command.Target, StringComparison.OrdinalIgnoreCase));
            if (!allowed) return Fail("Target is not in the agent allowlist.");
            if (string.Equals(command.Type, "KillProcess", StringComparison.OrdinalIgnoreCase))
            {
                var processes = System.Diagnostics.Process.GetProcessesByName(command.Target);
                if (processes.Length == 0) return Success("Process was already stopped.");
                foreach (var process in processes) { process.Kill(entireProcessTree: true); process.WaitForExit(3000); process.Dispose(); }
                return Success($"Stopped {processes.Length} process instance(s).");
            }
            if (string.Equals(command.Type, "StartDemoService", StringComparison.OrdinalIgnoreCase) && string.Equals(command.Target, "OpsForge.DemoService", StringComparison.OrdinalIgnoreCase))
            {
                var existing = System.Diagnostics.Process.GetProcessesByName("OpsForge.DemoService");
                if (existing.Length > 0) { foreach (var item in existing) item.Dispose(); return Success("Demo service is already running."); }
                var root = Environment.GetEnvironmentVariable("OPSFORGE_ROOT");
                if (string.IsNullOrWhiteSpace(root)) return Fail("OPSFORGE_ROOT is not set. Start the lab with START-HERE.cmd.");
                var project = Path.Combine(root, "OpsForge.DemoService", "OpsForge.DemoService.csproj");
                if (!File.Exists(project)) return Fail($"Demo project not found: {project}");
                var psi = new System.Diagnostics.ProcessStartInfo { FileName = "dotnet", WorkingDirectory = root, UseShellExecute = true, Arguments = $"run --project \"{project}\" --no-build" };
                var process = System.Diagnostics.Process.Start(psi);
                return process is null ? Fail("Process.Start returned null.") : Success("Demo service restart requested. Waiting for telemetry verification.");
            }
            return Fail($"Unsupported command type: {command.Type}");
        }
        catch (Exception ex) { return Fail($"{ex.GetType().Name}: {ex.Message}"); }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static CommandResultRequest Success(string message) => new() { Success = true, Message = message, CompletedUtc = DateTimeOffset.UtcNow };
    private static CommandResultRequest Fail(string message) => new() { Success = false, Message = message, CompletedUtc = DateTimeOffset.UtcNow };
}

internal sealed class AgentOptions
{
    public string ServerUrl { get; set; } = "http://localhost:5080";
    public string AgentId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Site { get; set; } = "Local Lab";
    public string EnvironmentName { get; set; } = "lab";
    public string ApiKey { get; set; } = string.Empty;
    public string EnrollmentToken { get; set; } = string.Empty;
    public string CredentialsFile { get; set; } = string.Empty;
    public bool AllowInsecureRemoteHttp { get; set; }
    public bool UseClientCertificate { get; set; } = true;
    public string ClientCertificatePfx { get; set; } = string.Empty;
    public string ClientCertificatePassword { get; set; } = string.Empty;
    public int HeartbeatSeconds { get; set; } = 3;
    public List<string> MonitoredProcesses { get; set; } = new() { "OpsForge.DemoService" };
    public List<string> MonitoredServices { get; set; } = new() { "EventLog" };
    public List<HealthCheckOptions> HealthChecks { get; set; } = new();

    public static AgentOptions Load()
    {
        var configuredPath = Environment.GetEnvironmentVariable("OPSFORGE_AGENT_CONFIG");
        var path = string.IsNullOrWhiteSpace(configuredPath) ? Path.Combine(AppContext.BaseDirectory, "appsettings.json") : Path.GetFullPath(configuredPath);
        if (!File.Exists(path)) return new AgentOptions();
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AgentOptions>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AgentOptions();
    }
}

internal sealed class HealthCheckOptions
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public int? Port { get; set; }
    public int TimeoutMs { get; set; } = 2000;
}

internal sealed class AgentCredentialFile
{
    public string AgentId { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string CredentialFingerprint { get; set; } = string.Empty;
    public string ClientCertificatePath { get; set; } = string.Empty;
    public string ClientCertificatePassword { get; set; } = string.Empty;
    public string ClientCertificateThumbprint { get; set; } = string.Empty;
    public DateTimeOffset SavedUtc { get; set; }
}

internal sealed record ClientCertificateMaterial(string Path, string Password, X509Certificate2 Certificate) : IDisposable
{
    public void Dispose() => Certificate.Dispose();
}
