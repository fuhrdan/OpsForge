namespace OpsForge.Server;

public sealed class SloDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public double ObjectivePercent { get; set; } = 99.9;
    public int ComplianceWindowHours { get; set; } = 720;
    public List<string> AgentIds { get; set; } = new();
    public List<SloBurnRatePolicy> BurnRatePolicies { get; set; } = new();
}

public sealed class SloBurnRatePolicy
{
    public string Id { get; set; } = string.Empty;
    public int ShortWindowMinutes { get; set; } = 5;
    public int LongWindowMinutes { get; set; } = 60;
    public double ShortBurnRateThreshold { get; set; } = 14.4;
    public double LongBurnRateThreshold { get; set; } = 14.4;
    public string Severity { get; set; } = "critical";
}

public sealed class SloWindowMeasurement
{
    public int WindowMinutes { get; set; }
    public long MonitoredSeconds { get; set; }
    public long DowntimeSeconds { get; set; }
    public double AvailabilityPercent { get; set; }
    public double ErrorFraction { get; set; }
    public double AllowedErrorFraction { get; set; }
    public double BurnRate { get; set; }
    public bool HasData { get; set; }
}

public sealed class SloBurnRateEvaluation
{
    public string PolicyId { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public SloWindowMeasurement ShortWindow { get; set; } = new();
    public SloWindowMeasurement LongWindow { get; set; } = new();
    public double ShortThreshold { get; set; }
    public double LongThreshold { get; set; }
    public bool Alerting { get; set; }
}

public sealed class SloEvaluationDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public double ObjectivePercent { get; set; }
    public int ComplianceWindowHours { get; set; }
    public IReadOnlyList<string> AgentIds { get; set; } = Array.Empty<string>();
    public DateTimeOffset EvaluatedUtc { get; set; }
    public SloWindowMeasurement ComplianceWindow { get; set; } = new();
    public double ErrorBudgetConsumedPercent { get; set; }
    public double ErrorBudgetRemainingPercent { get; set; }
    public string Status { get; set; } = string.Empty;
    public List<SloBurnRateEvaluation> BurnRates { get; set; } = new();
}

public sealed class SloStatusDto
{
    public bool Enabled { get; set; }
    public int EvaluationIntervalSeconds { get; set; }
    public int DefinitionCount { get; set; }
    public int EnabledDefinitionCount { get; set; }
    public List<SloDefinitionStatusDto> Definitions { get; set; } = new();
}

public sealed class SloDefinitionStatusDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public double ObjectivePercent { get; set; }
    public int ComplianceWindowHours { get; set; }
    public IReadOnlyList<string> AgentIds { get; set; } = Array.Empty<string>();
    public int BurnRatePolicyCount { get; set; }
}
