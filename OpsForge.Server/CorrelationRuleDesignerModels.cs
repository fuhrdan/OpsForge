using OpsForge.Contracts;

namespace OpsForge.Server;

public sealed class CorrelationRuleSetDto
{
    public List<CorrelationRule> Rules { get; set; } = new();
}

public sealed class CorrelationRuleValidationIssueDto
{
    public string RuleId { get; set; } = string.Empty;
    public string Field { get; set; } = string.Empty;
    public string Severity { get; set; } = "error";
    public string Message { get; set; } = string.Empty;
}

public sealed class CorrelationRuleValidationResultDto
{
    public bool Valid { get; set; }
    public int RuleCount { get; set; }
    public List<CorrelationRuleValidationIssueDto> Issues { get; set; } = new();
}

public sealed class CorrelationRulePreviewRequest
{
    public string AgentId { get; set; } = string.Empty;
    public List<CorrelationRule> Rules { get; set; } = new();
}

public sealed class CorrelationRulePreviewItemDto
{
    public string RuleId { get; set; } = string.Empty;
    public string RuleTitle { get; set; } = string.Empty;
    public bool Complete { get; set; }
    public int FailureCount { get; set; }
    public bool WouldOpenPrimaryIncident { get; set; }
    public PrimaryIncidentDto? Candidate { get; set; }
}

public sealed class CorrelationRulePreviewResultDto
{
    public string AgentId { get; set; } = string.Empty;
    public DateTimeOffset HeartbeatUtc { get; set; }
    public CorrelationRuleValidationResultDto Validation { get; set; } = new();
    public List<CorrelationRulePreviewItemDto> Results { get; set; } = new();
}

public sealed class CorrelationRuleImportRequest
{
    public List<CorrelationRule> Rules { get; set; } = new();
    public string Note { get; set; } = string.Empty;
}

public sealed class CorrelationRuleHistoryEntryDto
{
    public string VersionId { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public int RuleCount { get; set; }
    public List<CorrelationRule> Rules { get; set; } = new();
}

public sealed class CorrelationRuleExportDto
{
    public string Format { get; set; } = "opsforge-correlation-rules/v1";
    public DateTimeOffset ExportedUtc { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public List<CorrelationRule> Rules { get; set; } = new();
}
