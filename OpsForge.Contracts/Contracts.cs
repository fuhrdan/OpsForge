namespace OpsForge.Contracts;

public sealed class DriveMetric
{
    public string Name { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public double UsedPercent { get; set; }
}

public sealed class NetworkAdapterMetric
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Ipv4Address { get; set; }
    public long SpeedBitsPerSecond { get; set; }
    public string Status { get; set; } = string.Empty;
}

public sealed class ProcessMetric
{
    public string Name { get; set; } = string.Empty;
    public bool Running { get; set; }
    public int? ProcessId { get; set; }
}

public sealed class ServiceMetric
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public string Status { get; set; } = string.Empty;
}

public sealed class ProbeMetric
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public bool Success { get; set; }
    public long LatencyMs { get; set; }
    public string Detail { get; set; } = string.Empty;
    public DateTimeOffset CheckedUtc { get; set; }
}

public sealed class AgentHeartbeatRequest
{
    public string AgentId { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Site { get; set; } = string.Empty;
    public string EnvironmentName { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; set; }
    public double CpuPercent { get; set; }
    public double MemoryUsedPercent { get; set; }
    public long UptimeSeconds { get; set; }
    public List<DriveMetric> Drives { get; set; } = new();
    public List<NetworkAdapterMetric> NetworkAdapters { get; set; } = new();
    public List<ProcessMetric> MonitoredProcesses { get; set; } = new();
    public List<ServiceMetric> MonitoredServices { get; set; } = new();
    public List<ProbeMetric> Probes { get; set; } = new();
}

public sealed class AgentSnapshotDto
{
    public AgentHeartbeatRequest Heartbeat { get; set; } = new();
    public DateTimeOffset LastSeenUtc { get; set; }
    public bool Online { get; set; }
}

public sealed class IncidentDto
{
    public string Id { get; set; } = string.Empty;
    public string RuleKey { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Evidence { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public DateTimeOffset? ResolvedUtc { get; set; }
    public bool Active { get; set; }
    public long DurationSeconds { get; set; }
    public bool Suppressed { get; set; }
    public string? SuppressionReason { get; set; }
    public string? SuppressedByIncidentId { get; set; }
    public bool MaintenanceSuppressed { get; set; }
    public string? MaintenanceWindowName { get; set; }
}

public sealed class TimelineEventDto
{
    public long EventId { get; set; }
    public string AgentId { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; set; }
}

public sealed class AgentCommandDto
{
    public Guid CommandId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
}

public sealed class CommandResultRequest
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset CompletedUtc { get; set; }
}

public sealed class CommandStatusDto
{
    public Guid CommandId { get; set; }
    public string AgentId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? DeliveredUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public bool? Success { get; set; }
    public string? ResultMessage { get; set; }
    public string VerificationStatus { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public DateTimeOffset? VerifiedUtc { get; set; }
    public string? VerificationMessage { get; set; }
}

public sealed class RemediationPreviewDto
{
    public Guid PreviewToken { get; set; }
    public string AgentId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Risk { get; set; } = string.Empty;
    public string VerificationPlan { get; set; } = string.Empty;
    public DateTimeOffset ExpiresUtc { get; set; }
}


public sealed class CorrelatedSignalDto
{
    public string SignalKey { get; set; } = string.Empty;
    public string SignalType { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Evidence { get; set; } = string.Empty;
}

public sealed class PrimaryIncidentDto
{
    public string Id { get; set; } = string.Empty;
    public string CorrelationKey { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string ProbableRootCause { get; set; } = string.Empty;
    public string BlastRadius { get; set; } = string.Empty;
    public string Confidence { get; set; } = string.Empty;
    public double ConfidenceScore { get; set; }
    public List<CorrelatedSignalDto> Signals { get; set; } = new();
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public DateTimeOffset? ResolvedUtc { get; set; }
    public bool Active { get; set; }
    public long DurationSeconds { get; set; }
    public bool Acknowledged { get; set; }
    public string AcknowledgedBy { get; set; } = string.Empty;
    public DateTimeOffset? AcknowledgedUtc { get; set; }
    public string OwnerUsername { get; set; } = string.Empty;
    public string OwnerDisplayName { get; set; } = string.Empty;
    public DateTimeOffset? AssignedUtc { get; set; }
    public bool MaintenanceSuppressed { get; set; }
    public string MaintenanceWindowName { get; set; } = string.Empty;
}


public sealed class TopologyNodeDto
{
    public string Id { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string Health { get; set; } = string.Empty;
    public string Site { get; set; } = string.Empty;
    public string EnvironmentName { get; set; } = string.Empty;
}

public sealed class TopologyEdgeDto
{
    public string FromNodeId { get; set; } = string.Empty;
    public string ToNodeId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public bool CrossAgent { get; set; }
}

public sealed class TopologyImpactDto
{
    public string RootNodeId { get; set; } = string.Empty;
    public string RootLabel { get; set; } = string.Empty;
    public List<string> AffectedNodeIds { get; set; } = new();
    public List<string> AffectedLabels { get; set; } = new();
}

public sealed class TopologySnapshotDto
{
    public DateTimeOffset GeneratedUtc { get; set; }
    public int AgentsOnline { get; set; }
    public int AgentsTotal { get; set; }
    public int HealthyNodes { get; set; }
    public int FailedNodes { get; set; }
    public int SuppressedSignalCount { get; set; }
    public List<TopologyNodeDto> Nodes { get; set; } = new();
    public List<TopologyEdgeDto> Edges { get; set; } = new();
    public List<TopologyImpactDto> Impacts { get; set; } = new();
}

public sealed class OperatorSummaryDto
{
    public int ActivePrimaryIncidents { get; set; }
    public int ActionableSignals { get; set; }
    public int SuppressedDerivativeSignals { get; set; }
    public int AgentsOnline { get; set; }
    public int AgentsTotal { get; set; }
    public int AcknowledgedPrimaryIncidents { get; set; }
    public int UnownedPrimaryIncidents { get; set; }
    public int MaintenanceMutedIncidents { get; set; }
}

public sealed class IncidentAcknowledgeRequest
{
    public string Note { get; set; } = string.Empty;
}

public sealed class IncidentAssignmentRequest
{
    public string OwnerUsername { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
}

public sealed class MaintenanceWindowCreateRequest
{
    public string Name { get; set; } = string.Empty;
    public string AgentId { get; set; } = "*";
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
}

public sealed class MaintenanceWindowDto
{
    public string MaintenanceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string AgentId { get; set; } = "*";
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public bool Cancelled { get; set; }
    public string CancelledBy { get; set; } = string.Empty;
    public DateTimeOffset? CancelledUtc { get; set; }
    public bool ActiveNow { get; set; }
}

public sealed class TelemetryHistoryPointDto
{
    public DateTimeOffset TimestampUtc { get; set; }
    public double CpuPercent { get; set; }
    public double MemoryUsedPercent { get; set; }
    public int ProbeTotal { get; set; }
    public int ProbeFailed { get; set; }
    public double ProbeSuccessPercent { get; set; }
    public double ProbeAverageLatencyMs { get; set; }
}

public sealed class ReliabilityPointDto
{
    public DateTimeOffset TimestampUtc { get; set; }
    public double CpuAveragePercent { get; set; }
    public double MemoryAveragePercent { get; set; }
    public double AvailabilityPercent { get; set; }
    public double ProbeSuccessPercent { get; set; }
    public double ProbeAverageLatencyMs { get; set; }
}

public sealed class AgentReliabilityDto
{
    public string AgentId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Site { get; set; } = string.Empty;
    public string EnvironmentName { get; set; } = string.Empty;
    public bool Online { get; set; }
    public double AvailabilityPercent { get; set; }
    public long MonitoredSeconds { get; set; }
    public long MaintenanceExcludedSeconds { get; set; }
    public double CpuAveragePercent { get; set; }
    public double CpuPeakPercent { get; set; }
    public double MemoryAveragePercent { get; set; }
    public double MemoryPeakPercent { get; set; }
    public double ProbeSuccessPercent { get; set; }
    public double ProbeAverageLatencyMs { get; set; }
    public int IncidentsOpened { get; set; }
    public double AverageMttrSeconds { get; set; }
}

public sealed class IncidentTrendPointDto
{
    public DateTimeOffset BucketStartUtc { get; set; }
    public int Opened { get; set; }
    public int Resolved { get; set; }
    public double AverageMttrSeconds { get; set; }
}

public sealed class ReliabilityDashboardDto
{
    public DateTimeOffset GeneratedUtc { get; set; }
    public int RangeHours { get; set; }
    public double SlaTargetPercent { get; set; }
    public double FleetAvailabilityPercent { get; set; }
    public double ErrorBudgetRemainingPercent { get; set; }
    public long MonitoredSeconds { get; set; }
    public long DowntimeSeconds { get; set; }
    public long MaintenanceExcludedSeconds { get; set; }
    public int PrimaryIncidentsOpened { get; set; }
    public int PrimaryIncidentsResolved { get; set; }
    public double AverageMttrSeconds { get; set; }
    public int ActiveMaintenanceWindows { get; set; }
    public List<AgentReliabilityDto> Agents { get; set; } = new();
    public List<ReliabilityPointDto> Timeline { get; set; } = new();
    public List<IncidentTrendPointDto> IncidentTrend { get; set; } = new();
}

public sealed class AgentHistoryDto
{
    public string AgentId { get; set; } = string.Empty;
    public int RangeHours { get; set; }
    public List<TelemetryHistoryPointDto> Points { get; set; } = new();
}

public sealed class AgentEnrollmentRequest
{
    public string AgentId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Site { get; set; } = string.Empty;
    public string EnvironmentName { get; set; } = string.Empty;
    public string ClientCertificateThumbprint { get; set; } = string.Empty;
}

public sealed class AgentEnrollmentResponse
{
    public string AgentId { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string CredentialFingerprint { get; set; } = string.Empty;
    public DateTimeOffset EnrolledUtc { get; set; }
    public string Note { get; set; } = string.Empty;
    public string ClientCertificateThumbprint { get; set; } = string.Empty;
}

public sealed class AgentInventoryDto
{
    public string AgentId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Site { get; set; } = string.Empty;
    public string EnvironmentName { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = string.Empty;
    public string CredentialFingerprint { get; set; } = string.Empty;
    public DateTimeOffset EnrolledUtc { get; set; }
    public DateTimeOffset? FirstSeenUtc { get; set; }
    public DateTimeOffset? LastSeenUtc { get; set; }
    public string LastIpAddress { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool Online { get; set; }
    public bool Revoked { get; set; }
    public DateTimeOffset? RevokedUtc { get; set; }
    public string ClientCertificateThumbprint { get; set; } = string.Empty;
}

public sealed class AgentStatusHistoryDto
{
    public long EventId { get; set; }
    public string AgentId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; set; }
}

public sealed class SecurityStatusDto
{
    public bool AgentAuthenticationRequired { get; set; }
    public bool EnrollmentTokenRequired { get; set; }
    public bool UserAuthenticationRequired { get; set; }
    public bool RoleBasedAccessControl { get; set; }
    public bool AgentMtlsEnabled { get; set; }
    public bool Https { get; set; }
    public string TransportGuidance { get; set; } = string.Empty;
    public string EnrollmentTokenLocation { get; set; } = string.Empty;
    public string BootstrapAdminLocation { get; set; } = string.Empty;
    public string[] Roles { get; set; } = Array.Empty<string>();
}

public sealed class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class ChangePasswordRequest
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

public sealed class CreateOperatorUserRequest
{
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = "viewer";
}

public sealed class SetUserEnabledRequest
{
    public bool Enabled { get; set; }
}

public sealed class ResetPasswordResponse
{
    public string Username { get; set; } = string.Empty;
    public string TemporaryPassword { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
}

public sealed class OperatorUserDto
{
    public long UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool MustChangePassword { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? LastLoginUtc { get; set; }
    public DateTimeOffset? PasswordChangedUtc { get; set; }
}

public sealed class AuthSessionDto
{
    public bool Authenticated { get; set; }
    public OperatorUserDto? User { get; set; }
    public string CsrfToken { get; set; } = string.Empty;
    public DateTimeOffset? ExpiresUtc { get; set; }
}

public sealed class AuditEventDto
{
    public long AuditId { get; set; }
    public string ActorUsername { get; set; } = string.Empty;
    public string ActorDisplayName { get; set; } = string.Empty;
    public string ActorRole { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string RemoteIpAddress { get; set; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; set; }
}

public sealed class BindAgentCertificateRequest
{
    public string Thumbprint { get; set; } = string.Empty;
}
