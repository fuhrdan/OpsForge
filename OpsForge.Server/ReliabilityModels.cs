namespace OpsForge.Server;

public sealed class TelemetrySampleRecord
{
    public long SampleId { get; set; }
    public string AgentId { get; set; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; set; }
    public double CpuPercent { get; set; }
    public double MemoryUsedPercent { get; set; }
    public int ProbeTotal { get; set; }
    public int ProbeFailed { get; set; }
    public double ProbeAverageLatencyMs { get; set; }
}

public sealed class IncidentWorkflowRecord
{
    public string IncidentId { get; set; } = string.Empty;
    public bool Acknowledged { get; set; }
    public string AcknowledgedBy { get; set; } = string.Empty;
    public DateTimeOffset? AcknowledgedUtc { get; set; }
    public string OwnerUsername { get; set; } = string.Empty;
    public string OwnerDisplayName { get; set; } = string.Empty;
    public DateTimeOffset? AssignedUtc { get; set; }
    public string LastNote { get; set; } = string.Empty;
    public DateTimeOffset UpdatedUtc { get; set; }
}
