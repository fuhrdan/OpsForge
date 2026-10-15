namespace OpsForge.Server;

public sealed class NotificationEnvelope
{
    public string EventType { get; set; } = string.Empty;
    public string TargetRouteId { get; set; } = string.Empty;
    public string IncidentId { get; set; } = string.Empty;
    public string CorrelationKey { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string FleetServiceId { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string ProbableRootCause { get; set; } = string.Empty;
    public string BlastRadius { get; set; } = string.Empty;
    public string TraceId { get; set; } = string.Empty;
    public bool MaintenanceSuppressed { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string OwnerUsername { get; set; } = string.Empty;
    public DateTimeOffset OccurredUtc { get; set; }

    public static NotificationEnvelope FromPrimary(
        string eventType,
        OpsForge.Contracts.PrimaryIncidentDto incident,
        DateTimeOffset occurredUtc,
        string actor = "")
    {
        return new NotificationEnvelope
        {
            EventType = eventType,
            IncidentId = incident.Id,
            CorrelationKey = incident.CorrelationKey,
            AgentId = incident.AgentId,
            FleetServiceId = incident.FleetServiceId,
            Severity = string.IsNullOrWhiteSpace(incident.Severity) ? "critical" : incident.Severity,
            Title = incident.Title,
            Summary = incident.Summary,
            ProbableRootCause = incident.ProbableRootCause,
            BlastRadius = incident.BlastRadius,
            TraceId = incident.TraceId,
            MaintenanceSuppressed = incident.MaintenanceSuppressed,
            Actor = actor,
            OwnerUsername = incident.OwnerUsername,
            OccurredUtc = occurredUtc
        };
    }
}

public sealed class NotificationDeliveryDto
{
    public long DeliveryId { get; set; }
    public string RouteId { get; set; } = string.Empty;
    public string ChannelType { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string IncidentId { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string DestinationLabel { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public int Attempt { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
}

public sealed class NotificationTestRequest
{
    public string RouteId { get; set; } = string.Empty;
}

public sealed class NotificationStatusDto
{
    public bool Enabled { get; set; }
    public int RouteCount { get; set; }
    public int EnabledRouteCount { get; set; }
    public int EscalationSeconds { get; set; }
    public List<NotificationRouteStatusDto> Routes { get; set; } = new();
}

public sealed class NotificationRouteStatusDto
{
    public string Id { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string ChannelType { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public string MinimumSeverity { get; set; } = string.Empty;
    public IReadOnlyList<string> EventTypes { get; set; } = Array.Empty<string>();
    public string Destination { get; set; } = string.Empty;
    public bool SecretConfigured { get; set; }
}
