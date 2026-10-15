using System.Text.Json;

namespace OpsForge.Server;

public sealed class NotificationConfiguration
{
    public bool Enabled { get; set; }
    public int DefaultCooldownSeconds { get; set; } = 300;
    public int EscalationSeconds { get; set; } = 900;
    public List<NotificationRoute> Routes { get; set; } = new();
    public SmtpNotificationOptions Smtp { get; set; } = new();

    public static NotificationConfiguration Load(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, "notification-rules.json");
        if (!File.Exists(path))
            return new NotificationConfiguration();

        var json = File.ReadAllText(path);
        var document = JsonSerializer.Deserialize<NotificationConfigurationRoot>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var configuration = document?.Notification ?? new NotificationConfiguration();
        configuration.Validate();
        return configuration;
    }

    public void Validate()
    {
        DefaultCooldownSeconds = Math.Clamp(DefaultCooldownSeconds, 0, 86400);
        EscalationSeconds = Math.Clamp(EscalationSeconds, 60, 604800);

        var routeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in Routes)
        {
            if (string.IsNullOrWhiteSpace(route.Id) || !routeIds.Add(route.Id))
                throw new InvalidOperationException("Notification routes require unique non-empty IDs.");

            route.ChannelType = route.ChannelType.Trim().ToLowerInvariant();
            route.Format = string.IsNullOrWhiteSpace(route.Format) ? "generic" : route.Format.Trim().ToLowerInvariant();
            route.MinimumSeverity = string.IsNullOrWhiteSpace(route.MinimumSeverity) ? "warning" : route.MinimumSeverity.Trim().ToLowerInvariant();

            if (route.ChannelType is not ("webhook" or "email"))
                throw new InvalidOperationException($"Notification route {route.Id} has unsupported channel type {route.ChannelType}.");

            if (route.ChannelType == "webhook" && string.IsNullOrWhiteSpace(route.TargetEnvironmentVariable))
                throw new InvalidOperationException($"Webhook route {route.Id} must name a targetEnvironmentVariable.");

            if (route.ChannelType == "email" && route.Recipients.Count == 0)
                throw new InvalidOperationException($"Email route {route.Id} requires at least one recipient.");

            if (route.EventTypes.Count == 0)
                route.EventTypes = new() { "Opened", "Resolved", "Escalated" };
        }
    }

    public NotificationStatusDto GetStatus()
    {
        return new NotificationStatusDto
        {
            Enabled = Enabled,
            RouteCount = Routes.Count,
            EnabledRouteCount = Routes.Count(r => r.Enabled),
            EscalationSeconds = EscalationSeconds,
            Routes = Routes.Select(route => new NotificationRouteStatusDto
            {
                Id = route.Id,
                Enabled = route.Enabled,
                ChannelType = route.ChannelType,
                Format = route.Format,
                MinimumSeverity = route.MinimumSeverity,
                EventTypes = route.EventTypes,
                Destination = route.ChannelType == "email"
                    ? string.Join(", ", route.Recipients)
                    : route.TargetEnvironmentVariable,
                SecretConfigured = route.ChannelType == "email"
                    ? !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Smtp.PasswordEnvironmentVariable))
                    : !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(route.TargetEnvironmentVariable))
            }).ToList()
        };
    }

    public sealed class NotificationConfigurationRoot
    {
        public NotificationConfiguration Notification { get; set; } = new();
    }
}

public sealed class NotificationRoute
{
    public string Id { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string ChannelType { get; set; } = "webhook";
    public string Format { get; set; } = "generic";
    public string TargetEnvironmentVariable { get; set; } = string.Empty;
    public List<string> Recipients { get; set; } = new();
    public List<string> EventTypes { get; set; } = new();
    public string MinimumSeverity { get; set; } = "warning";
    public List<string> AgentIds { get; set; } = new();
    public List<string> FleetServiceIds { get; set; } = new();
    public int? CooldownSeconds { get; set; }
    public bool SuppressDuringMaintenance { get; set; } = true;

    public bool Matches(NotificationEnvelope envelope)
    {
        if (!Enabled) return false;
        if (!string.IsNullOrWhiteSpace(envelope.TargetRouteId) &&
            !string.Equals(envelope.TargetRouteId, Id, StringComparison.OrdinalIgnoreCase)) return false;
        if (!EventTypes.Any(value => string.Equals(value, envelope.EventType, StringComparison.OrdinalIgnoreCase))) return false;
        if (SeverityRank(envelope.Severity) < SeverityRank(MinimumSeverity)) return false;
        if (SuppressDuringMaintenance && envelope.MaintenanceSuppressed) return false;

        if (AgentIds.Count > 0 &&
            !AgentIds.Any(value => string.Equals(value, envelope.AgentId, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (FleetServiceIds.Count > 0 &&
            !FleetServiceIds.Any(value => string.Equals(value, envelope.FleetServiceId, StringComparison.OrdinalIgnoreCase)))
            return false;

        return true;
    }

    public static int SeverityRank(string severity) => severity.Trim().ToLowerInvariant() switch
    {
        "critical" => 3,
        "warning" => 2,
        "info" => 1,
        _ => 0
    };
}

public sealed class SmtpNotificationOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string PasswordEnvironmentVariable { get; set; } = "OPSFORGE_SMTP_PASSWORD";
    public string FromAddress { get; set; } = string.Empty;
    public string FromDisplayName { get; set; } = "OpsForge";
}
