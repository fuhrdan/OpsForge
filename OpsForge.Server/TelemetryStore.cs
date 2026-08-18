using System.Collections.Concurrent;
using OpsForge.Contracts;

namespace OpsForge.Server;

public sealed class TelemetryStore
{
    private readonly ConcurrentDictionary<string, AgentState> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, CommandRecord> _commands = new();
    private readonly ConcurrentDictionary<Guid, PreviewRecord> _previews = new();
    private readonly SqliteRepository _repository;

    public TelemetryStore(SqliteRepository repository)
    {
        _repository = repository;
    }

    public void RecordHeartbeat(AgentHeartbeatRequest heartbeat)
    {
        var now = DateTimeOffset.UtcNow;
        _agents[heartbeat.AgentId] = new AgentState(heartbeat, now);
        _repository.RecordTelemetrySample(heartbeat, now);

        UpdateIncident(
            $"{heartbeat.AgentId}:offline",
            heartbeat.AgentId,
            false,
            "critical",
            "availability",
            $"Agent {heartbeat.MachineName} is offline",
            $"Heartbeat restored at {now:O}.",
            "Verify the host and OpsForge.Agent process are running.",
            now);

        UpdateIncident(
            $"{heartbeat.AgentId}:cpu",
            heartbeat.AgentId,
            heartbeat.CpuPercent >= 90.0,
            "warning",
            "performance",
            $"High CPU on {heartbeat.MachineName}",
            $"CPU utilization is {heartbeat.CpuPercent:F1}%.",
            "Inspect the highest-CPU processes and verify whether the load is expected.",
            now);

        UpdateIncident(
            $"{heartbeat.AgentId}:memory",
            heartbeat.AgentId,
            heartbeat.MemoryUsedPercent >= 90.0,
            "warning",
            "performance",
            $"High memory usage on {heartbeat.MachineName}",
            $"Memory utilization is {heartbeat.MemoryUsedPercent:F1}%.",
            "Inspect memory-heavy processes and check for sustained growth or leaks.",
            now);

        foreach (var drive in heartbeat.Drives)
        {
            UpdateIncident(
                $"{heartbeat.AgentId}:disk:{drive.Name}",
                heartbeat.AgentId,
                drive.UsedPercent >= 90.0,
                "warning",
                "capacity",
                $"Low disk space on {heartbeat.MachineName} {drive.Name}",
                $"Disk utilization is {drive.UsedPercent:F1}%.",
                "Remove unnecessary files or increase capacity before the volume fills.",
                now);
        }

        foreach (var process in heartbeat.MonitoredProcesses)
        {
            UpdateIncident(
                $"{heartbeat.AgentId}:process:{process.Name}",
                heartbeat.AgentId,
                !process.Running,
                "critical",
                "service",
                $"{process.Name} is down on {heartbeat.MachineName}",
                process.Running
                    ? $"Telemetry confirms {process.Name} is running with PID {process.ProcessId}."
                    : $"The monitored process {process.Name} is not running.",
                $"Restart {process.Name}, then verify that its dependent health checks recover.",
                now);
        }

        foreach (var service in heartbeat.MonitoredServices)
        {
            var healthy = service.Exists && string.Equals(service.Status, "Running", StringComparison.OrdinalIgnoreCase);
            UpdateIncident(
                $"{heartbeat.AgentId}:windows-service:{service.Name}",
                heartbeat.AgentId,
                !healthy,
                "critical",
                "windows-service",
                $"Windows service {service.Name} is unhealthy on {heartbeat.MachineName}",
                healthy
                    ? $"Telemetry confirms {service.DisplayName} ({service.Name}) is Running."
                    : service.Exists
                        ? $"{service.DisplayName} ({service.Name}) reports status {service.Status}."
                        : $"Windows service {service.Name} was not found.",
                "Confirm the service configuration and dependencies before attempting a controlled restart.",
                now);
        }

        foreach (var probe in heartbeat.Probes)
        {
            var type = probe.Type.ToUpperInvariant();
            var category = type switch
            {
                "DNS" => "dns",
                "TCP" => "network",
                _ => "availability"
            };

            UpdateIncident(
                $"{heartbeat.AgentId}:probe:{probe.Id}",
                heartbeat.AgentId,
                !probe.Success,
                type == "DNS" ? "warning" : "critical",
                category,
                $"{type} check {probe.Id} failed on {heartbeat.MachineName}",
                probe.Success
                    ? $"{type} probe to {probe.Target} recovered in {probe.LatencyMs} ms. {probe.Detail}"
                    : $"{type} probe to {probe.Target} failed after {probe.LatencyMs} ms. {probe.Detail}",
                GetProbeRecommendation(type),
                now);
        }

        EvaluatePrimaryIncident(heartbeat, now);
        VerifyCompletedCommands(heartbeat, now);
    }

    public IReadOnlyList<AgentSnapshotDto> GetAgents()
    {
        RefreshOfflineIncidents();
        var now = DateTimeOffset.UtcNow;

        return _agents.Values
            .OrderBy(state => state.Heartbeat.MachineName)
            .Select(state => new AgentSnapshotDto
            {
                Heartbeat = state.Heartbeat,
                LastSeenUtc = state.LastSeenUtc,
                Online = now - state.LastSeenUtc <= TimeSpan.FromSeconds(20)
            })
            .ToList();
    }

    public IReadOnlyList<IncidentDto> GetIncidents()
    {
        RefreshOfflineIncidents();
        var incidents = _repository.GetIncidents().ToList();
        var primaries = _repository.GetPrimaryIncidents().Where(i => i.Active).ToList();
        var suppression = primaries
            .SelectMany(primary => primary.Signals.Select(signal => new { primary, signal }))
            .GroupBy(item => item.signal.SignalKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().primary, StringComparer.OrdinalIgnoreCase);

        foreach (var incident in incidents)
        {
            if (incident.Active && suppression.TryGetValue(incident.RuleKey, out var primary))
            {
                incident.Suppressed = true;
                incident.SuppressedByIncidentId = primary.Id;
                incident.SuppressionReason = $"Derivative signal correlated under primary incident: {primary.Title}";
            }

            if (incident.Active && _repository.GetActiveMaintenanceWindow(incident.AgentId, DateTimeOffset.UtcNow) is { } maintenance)
            {
                incident.MaintenanceSuppressed = true;
                incident.MaintenanceWindowName = maintenance.Name;
                incident.Suppressed = true;
                incident.SuppressionReason = string.IsNullOrWhiteSpace(incident.SuppressionReason)
                    ? $"Muted by maintenance window: {maintenance.Name}"
                    : $"{incident.SuppressionReason} · Also muted by maintenance: {maintenance.Name}";
            }
        }
        return incidents;
    }

    public TopologySnapshotDto GetTopology()
    {
        var agents = GetAgents();
        var incidents = GetIncidents();
        return TopologyEngine.Build(agents, incidents);
    }

    public OperatorSummaryDto GetOperatorSummary()
    {
        var agents = GetAgents();
        var incidents = GetIncidents();
        var primaries = GetPrimaryIncidents();
        var activePrimaries = primaries.Where(i => i.Active).ToList();
        return new OperatorSummaryDto
        {
            ActivePrimaryIncidents = activePrimaries.Count(i => !i.MaintenanceSuppressed),
            ActionableSignals = incidents.Count(i => i.Active && !i.Suppressed),
            SuppressedDerivativeSignals = incidents.Count(i => i.Active && i.Suppressed && !i.MaintenanceSuppressed),
            AgentsOnline = agents.Count(a => a.Online),
            AgentsTotal = agents.Count,
            AcknowledgedPrimaryIncidents = activePrimaries.Count(i => i.Acknowledged),
            UnownedPrimaryIncidents = activePrimaries.Count(i => !i.MaintenanceSuppressed && string.IsNullOrWhiteSpace(i.OwnerUsername)),
            MaintenanceMutedIncidents = activePrimaries.Count(i => i.MaintenanceSuppressed)
        };
    }

    public IReadOnlyList<TimelineEventDto> GetTimelineEvents() => _repository.GetTimelineEvents();
    public IReadOnlyList<CommandStatusDto> GetCommands() => _repository.GetCommands();

    public IReadOnlyList<PrimaryIncidentDto> GetPrimaryIncidents()
    {
        var now = DateTimeOffset.UtcNow;
        var incidents = _repository.GetPrimaryIncidents().ToList();
        foreach (var incident in incidents)
        {
            if (_repository.GetIncidentWorkflow(incident.Id) is { } workflow)
            {
                incident.Acknowledged = workflow.Acknowledged;
                incident.AcknowledgedBy = workflow.AcknowledgedBy;
                incident.AcknowledgedUtc = workflow.AcknowledgedUtc;
                incident.OwnerUsername = workflow.OwnerUsername;
                incident.OwnerDisplayName = workflow.OwnerDisplayName;
                incident.AssignedUtc = workflow.AssignedUtc;
            }
            if (incident.Active && _repository.GetActiveMaintenanceWindow(incident.AgentId, now) is { } maintenance)
            {
                incident.MaintenanceSuppressed = true;
                incident.MaintenanceWindowName = maintenance.Name;
            }
        }
        return incidents;
    }

    public PrimaryIncidentDto? AcknowledgePrimaryIncident(string incidentId, string username, string note)
    {
        var incident = _repository.GetPrimaryIncidents().FirstOrDefault(i => string.Equals(i.Id, incidentId, StringComparison.OrdinalIgnoreCase));
        if (incident is null || !incident.Active) return null;
        var now = DateTimeOffset.UtcNow;
        _repository.AcknowledgeIncident(incident.Id, username, note, now);
        _repository.AddTimelineEvent(incident.AgentId, "primary-incident", incident.Id, "Acknowledged", $"Acknowledged: {incident.Title}", string.IsNullOrWhiteSpace(note) ? $"Acknowledged by {username}." : $"Acknowledged by {username}. {note}", now);
        return GetPrimaryIncidents().FirstOrDefault(i => string.Equals(i.Id, incidentId, StringComparison.OrdinalIgnoreCase));
    }

    public PrimaryIncidentDto? AssignPrimaryIncident(string incidentId, string ownerUsername, string ownerDisplayName, string assignedBy, string note)
    {
        var incident = _repository.GetPrimaryIncidents().FirstOrDefault(i => string.Equals(i.Id, incidentId, StringComparison.OrdinalIgnoreCase));
        if (incident is null || !incident.Active) return null;
        var now = DateTimeOffset.UtcNow;
        _repository.AssignIncident(incident.Id, ownerUsername, ownerDisplayName, note, now);
        _repository.AddTimelineEvent(incident.AgentId, "primary-incident", incident.Id, "Assigned", $"Owner assigned: {incident.Title}", string.IsNullOrWhiteSpace(note) ? $"{assignedBy} assigned incident ownership to {ownerDisplayName} ({ownerUsername})." : $"{assignedBy} assigned incident ownership to {ownerDisplayName} ({ownerUsername}). {note}", now);
        return GetPrimaryIncidents().FirstOrDefault(i => string.Equals(i.Id, incidentId, StringComparison.OrdinalIgnoreCase));
    }

    public PrimaryIncidentDto? UnassignPrimaryIncident(string incidentId, string actor, string note)
    {
        var incident = _repository.GetPrimaryIncidents().FirstOrDefault(i => string.Equals(i.Id, incidentId, StringComparison.OrdinalIgnoreCase));
        if (incident is null || !incident.Active) return null;
        var now = DateTimeOffset.UtcNow;
        _repository.UnassignIncident(incident.Id, note, now);
        _repository.AddTimelineEvent(incident.AgentId, "primary-incident", incident.Id, "Unassigned", $"Owner cleared: {incident.Title}", string.IsNullOrWhiteSpace(note) ? $"{actor} cleared incident ownership." : $"{actor} cleared incident ownership. {note}", now);
        return GetPrimaryIncidents().FirstOrDefault(i => string.Equals(i.Id, incidentId, StringComparison.OrdinalIgnoreCase));
    }

    public RemediationPreviewDto? CreateRestartDemoPreview(string agentId, string requestedBy)
    {
        if (!_agents.TryGetValue(agentId, out var agent) ||
            !agent.Heartbeat.MonitoredProcesses.Any(p =>
                string.Equals(p.Name, "OpsForge.DemoService", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var preview = new PreviewRecord
        {
            PreviewToken = Guid.NewGuid(),
            AgentId = agentId,
            Type = "StartDemoService",
            Target = "OpsForge.DemoService",
            CreatedUtc = now,
            ExpiresUtc = now.AddMinutes(2),
            RequestedBy = requestedBy
        };
        _previews[preview.PreviewToken] = preview;

        _repository.AddTimelineEvent(
            agentId,
            "remediation",
            preview.PreviewToken.ToString("D"),
            "Previewed",
            "Restart remediation previewed",
            "No action executed. OpsForge.DemoService restart was reviewed with a two-minute approval token.",
            now);

        return ToPreviewDto(preview);
    }

    public CommandStatusDto? ExecutePreview(Guid previewToken, string requestedBy)
    {
        if (!_previews.TryGetValue(previewToken, out var preview) || preview.Used)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (preview.ExpiresUtc < now)
        {
            _previews.TryRemove(previewToken, out _);
            return null;
        }

        preview.Used = true;
        var actor = string.IsNullOrWhiteSpace(requestedBy) ? preview.RequestedBy : requestedBy;
        var command = QueueCommand(preview.AgentId, preview.Type, preview.Target, "Approved from remediation preview", actor);
        _previews.TryRemove(previewToken, out _);
        return command is null ? null : ToCommandStatusDto(command);
    }

    public CommandStatusDto? QueueChaosKill(string agentId, string requestedBy)
    {
        return QueueCommand(agentId, "KillProcess", "OpsForge.DemoService", "Failure injected from Chaos Lab", requestedBy) is { } command
            ? ToCommandStatusDto(command)
            : null;
    }

    public AgentCommandDto? GetNextCommand(string agentId)
    {
        var now = DateTimeOffset.UtcNow;
        var command = _commands.Values
            .Where(c => string.Equals(c.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
            .Where(c => c.CompletedUtc is null)
            .Where(c => c.DeliveredUtc is null || now - c.DeliveredUtc > TimeSpan.FromSeconds(10))
            .OrderBy(c => c.CreatedUtc)
            .FirstOrDefault();

        if (command is null)
        {
            return null;
        }

        command.DeliveredUtc = now;
        command.DeliveryCount++;
        command.VerificationStatus = "Awaiting execution";
        PersistCommand(command);
        _repository.AddTimelineEvent(
            command.AgentId,
            "command",
            command.CommandId.ToString("D"),
            "Delivered",
            $"Command delivered: {command.Type}",
            $"Target {command.Target}; delivery attempt {command.DeliveryCount}.",
            now);

        return ToCommandDto(command);
    }

    public bool CompleteCommand(string agentId, Guid commandId, CommandResultRequest result)
    {
        if (!_commands.TryGetValue(commandId, out var command) ||
            !string.Equals(command.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        command.CompletedUtc = result.CompletedUtc == default ? DateTimeOffset.UtcNow : result.CompletedUtc;
        command.Success = result.Success;
        command.ResultMessage = result.Message;
        command.VerificationStatus = result.Success ? "Pending telemetry verification" : "Execution failed";
        command.VerificationMessage = result.Success
            ? "Waiting for a subsequent heartbeat to prove the requested state."
            : "The agent reported that the command did not execute successfully.";
        PersistCommand(command);

        _repository.AddTimelineEvent(
            command.AgentId,
            "command",
            command.CommandId.ToString("D"),
            result.Success ? "Executed" : "ExecutionFailed",
            result.Success ? $"Command executed: {command.Type}" : $"Command failed: {command.Type}",
            result.Message,
            command.CompletedUtc.Value);

        return true;
    }

    private CommandRecord? QueueCommand(string agentId, string type, string target, string reason, string requestedBy)
    {
        if (!_agents.TryGetValue(agentId, out var agent))
        {
            return null;
        }

        var registered = agent.Heartbeat.MonitoredProcesses
            .Any(p => string.Equals(p.Name, target, StringComparison.OrdinalIgnoreCase));

        if (!registered)
        {
            return null;
        }

        var record = new CommandRecord
        {
            CommandId = Guid.NewGuid(),
            AgentId = agentId,
            Type = type,
            Target = target,
            CreatedUtc = DateTimeOffset.UtcNow,
            VerificationStatus = "Queued",
            RequestedBy = requestedBy ?? string.Empty
        };

        _commands[record.CommandId] = record;
        PersistCommand(record);
        _repository.AddTimelineEvent(
            agentId,
            "command",
            record.CommandId.ToString("D"),
            "Queued",
            $"Command queued: {type}",
            $"Target {target}. {reason}.",
            record.CreatedUtc);
        return record;
    }

    private void VerifyCompletedCommands(AgentHeartbeatRequest heartbeat, DateTimeOffset now)
    {
        foreach (var command in _commands.Values
                     .Where(c => string.Equals(c.AgentId, heartbeat.AgentId, StringComparison.OrdinalIgnoreCase))
                     .Where(c => c.CompletedUtc.HasValue && c.Success == true && c.VerifiedUtc is null))
        {
            var process = heartbeat.MonitoredProcesses.FirstOrDefault(p =>
                string.Equals(p.Name, command.Target, StringComparison.OrdinalIgnoreCase));

            bool? verified = command.Type switch
            {
                "StartDemoService" when process is not null => process.Running,
                "KillProcess" when process is not null => !process.Running,
                _ => null
            };

            if (verified == true)
            {
                command.VerifiedUtc = now;
                command.VerificationStatus = "Verified";
                command.VerificationMessage = command.Type == "KillProcess"
                    ? $"Heartbeat confirms {command.Target} is stopped."
                    : $"Heartbeat confirms {command.Target} is running with PID {process?.ProcessId}.";
                PersistCommand(command);
                _repository.AddTimelineEvent(
                    command.AgentId,
                    "command",
                    command.CommandId.ToString("D"),
                    "Verified",
                    $"Remediation verified: {command.Type}",
                    command.VerificationMessage,
                    now);
            }
            else if (command.CompletedUtc.HasValue &&
                     now - command.CompletedUtc.Value > TimeSpan.FromSeconds(30) &&
                     command.VerificationStatus != "Verification failed")
            {
                command.VerificationStatus = "Verification failed";
                command.VerificationMessage = "The requested state was not observed within 30 seconds of execution.";
                PersistCommand(command);
                _repository.AddTimelineEvent(
                    command.AgentId,
                    "command",
                    command.CommandId.ToString("D"),
                    "VerificationFailed",
                    $"Verification failed: {command.Type}",
                    command.VerificationMessage,
                    now);
            }
        }
    }


    private void EvaluatePrimaryIncident(AgentHeartbeatRequest heartbeat, DateTimeOffset now)
    {
        var correlationKey = $"{heartbeat.AgentId}:primary:demo-application";
        var existing = _repository.GetActivePrimaryIncident(correlationKey);
        var candidate = CorrelationEngine.EvaluateDemoApplication(heartbeat, now);

        if (candidate is not null)
        {
            var topologyAgents = _agents.Values.Select(state => new AgentSnapshotDto
            {
                Heartbeat = state.Heartbeat,
                LastSeenUtc = state.LastSeenUtc,
                Online = now - state.LastSeenUtc <= TimeSpan.FromSeconds(20)
            }).ToList();
            candidate.BlastRadius = TopologyEngine.ExpandBlastRadius(heartbeat.AgentId, candidate.BlastRadius, topologyAgents);
        }

        if (candidate is not null)
        {
            if (existing is null)
            {
                candidate.Id = Guid.NewGuid().ToString("N");
                candidate.FirstSeenUtc = now;
                candidate.LastSeenUtc = now;
                _repository.InsertPrimaryIncident(candidate);
                _repository.AddTimelineEvent(
                    heartbeat.AgentId,
                    "primary-incident",
                    candidate.Id,
                    "Correlated",
                    candidate.Title,
                    $"{candidate.Summary} Probable root cause: {candidate.ProbableRootCause} Confidence: {candidate.Confidence} ({candidate.ConfidenceScore:P0}).",
                    now);
                return;
            }

            var oldSignature = PrimarySignature(existing);
            candidate.Id = existing.Id;
            candidate.FirstSeenUtc = existing.FirstSeenUtc;
            candidate.LastSeenUtc = now;
            _repository.TouchPrimaryIncident(candidate);

            if (!string.Equals(oldSignature, PrimarySignature(candidate), StringComparison.Ordinal))
            {
                _repository.AddTimelineEvent(
                    heartbeat.AgentId,
                    "primary-incident",
                    candidate.Id,
                    "Reassessed",
                    $"Diagnosis reassessed: {candidate.Title}",
                    $"Probable root cause: {candidate.ProbableRootCause} Confidence: {candidate.Confidence} ({candidate.ConfidenceScore:P0}). Active signals: {string.Join(", ", candidate.Signals.Select(signal => signal.SignalType))}.",
                    now);
            }

            return;
        }

        if (existing is not null)
        {
            var failedSignalCount = CountDemoFailures(heartbeat);
            var resolution = failedSignalCount == 0
                ? "Fresh telemetry confirms the process, TCP listener, and HTTP health endpoint are healthy."
                : $"The correlation cleared because only {failedSignalCount} failure signal remains; any remaining low-level signal stays open for troubleshooting.";

            _repository.ResolvePrimaryIncident(existing.Id, now, resolution);
            var duration = Math.Max(0L, (long)(now - existing.FirstSeenUtc).TotalSeconds);
            _repository.AddTimelineEvent(
                heartbeat.AgentId,
                "primary-incident",
                existing.Id,
                "Resolved",
                $"Resolved: {existing.Title}",
                $"{resolution} Correlated MTTR {FormatDuration(duration)}.",
                now);
        }
    }

    private static int CountDemoFailures(AgentHeartbeatRequest heartbeat)
    {
        var process = heartbeat.MonitoredProcesses.FirstOrDefault(p =>
            string.Equals(p.Name, "OpsForge.DemoService", StringComparison.OrdinalIgnoreCase));
        var http = heartbeat.Probes.FirstOrDefault(p =>
            string.Equals(p.Id, "demo-http", StringComparison.OrdinalIgnoreCase));
        var tcp = heartbeat.Probes.FirstOrDefault(p =>
            string.Equals(p.Id, "demo-tcp", StringComparison.OrdinalIgnoreCase));

        return (process is not null && !process.Running ? 1 : 0) +
               (http is not null && !http.Success ? 1 : 0) +
               (tcp is not null && !tcp.Success ? 1 : 0);
    }

    private static string PrimarySignature(PrimaryIncidentDto incident)
    {
        var signalSignature = string.Join("|", incident.Signals
            .OrderBy(signal => signal.SignalKey)
            .Select(signal => $"{signal.SignalKey}:{signal.Role}"));
        return $"{incident.ProbableRootCause}|{incident.Confidence}|{incident.ConfidenceScore:F2}|{signalSignature}";
    }

    private void RefreshOfflineIncidents()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _agents)
        {
            var state = pair.Value;
            var offline = now - state.LastSeenUtc > TimeSpan.FromSeconds(20);
            UpdateIncident(
                $"{pair.Key}:offline",
                pair.Key,
                offline,
                "critical",
                "availability",
                $"Agent {state.Heartbeat.MachineName} is offline",
                offline
                    ? $"Last heartbeat was received at {state.LastSeenUtc:O}."
                    : $"Heartbeat restored at {state.LastSeenUtc:O}.",
                "Verify host connectivity and restart OpsForge.Agent if necessary.",
                now);
        }
    }

    private void UpdateIncident(
        string ruleKey,
        string agentId,
        bool conditionActive,
        string severity,
        string category,
        string title,
        string evidence,
        string recommendation,
        DateTimeOffset now)
    {
        var existing = _repository.GetActiveIncident(ruleKey);

        if (conditionActive)
        {
            if (existing is null)
            {
                var incident = new IncidentDto
                {
                    Id = Guid.NewGuid().ToString("N"),
                    RuleKey = ruleKey,
                    AgentId = agentId,
                    Severity = severity,
                    Category = category,
                    Title = title,
                    Evidence = evidence,
                    Recommendation = recommendation,
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                    Active = true,
                    DurationSeconds = 0
                };
                _repository.InsertIncident(incident);
                _repository.AddTimelineEvent(
                    agentId,
                    "incident",
                    incident.Id,
                    "Opened",
                    title,
                    evidence,
                    now);
            }
            else
            {
                existing.Severity = severity;
                existing.Category = category;
                existing.Title = title;
                existing.Evidence = evidence;
                existing.Recommendation = recommendation;
                existing.LastSeenUtc = now;
                _repository.TouchIncident(existing);
            }
        }
        else if (existing is not null)
        {
            _repository.ResolveIncident(existing.Id, now, evidence);
            var duration = Math.Max(0L, (long)(now - existing.FirstSeenUtc).TotalSeconds);
            _repository.AddTimelineEvent(
                agentId,
                "incident",
                existing.Id,
                "Resolved",
                $"Resolved: {existing.Title}",
                $"{evidence} MTTR {FormatDuration(duration)}.",
                now);
        }
    }

    private void PersistCommand(CommandRecord command) => _repository.SaveCommand(ToCommandStatusDto(command));

    private static string GetProbeRecommendation(string type) => type switch
    {
        "DNS" => "Validate DNS server reachability, resolver configuration, and the requested record.",
        "TCP" => "Verify the destination host, listener, firewall path, and service binding for the target port.",
        _ => "Check the application endpoint, upstream dependencies, listener, and recent service changes."
    };

    private static string FormatDuration(long seconds)
    {
        if (seconds < 60) return $"{seconds}s";
        if (seconds < 3600) return $"{seconds / 60}m {seconds % 60}s";
        return $"{seconds / 3600}h {(seconds % 3600) / 60}m";
    }

    private static AgentCommandDto ToCommandDto(CommandRecord record) => new()
    {
        CommandId = record.CommandId,
        Type = record.Type,
        Target = record.Target,
        CreatedUtc = record.CreatedUtc
    };

    private static CommandStatusDto ToCommandStatusDto(CommandRecord record) => new()
    {
        CommandId = record.CommandId,
        AgentId = record.AgentId,
        Type = record.Type,
        Target = record.Target,
        CreatedUtc = record.CreatedUtc,
        DeliveredUtc = record.DeliveredUtc,
        CompletedUtc = record.CompletedUtc,
        Success = record.Success,
        ResultMessage = record.ResultMessage,
        VerificationStatus = record.VerificationStatus,
        VerifiedUtc = record.VerifiedUtc,
        VerificationMessage = record.VerificationMessage,
        RequestedBy = record.RequestedBy
    };

    private static RemediationPreviewDto ToPreviewDto(PreviewRecord record) => new()
    {
        PreviewToken = record.PreviewToken,
        AgentId = record.AgentId,
        Action = record.Type,
        Target = record.Target,
        Summary = "Start the allowlisted OpsForge.DemoService process. No arbitrary shell command is generated.",
        Risk = "Low · launches only the registered demo project through the existing constrained agent executor.",
        VerificationPlan = "Wait for fresh agent telemetry and require OpsForge.DemoService to report Running before marking remediation Verified.",
        ExpiresUtc = record.ExpiresUtc
    };

    private sealed record AgentState(AgentHeartbeatRequest Heartbeat, DateTimeOffset LastSeenUtc);

    private sealed class PreviewRecord
    {
        public Guid PreviewToken { get; set; }
        public string AgentId { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public DateTimeOffset CreatedUtc { get; set; }
        public DateTimeOffset ExpiresUtc { get; set; }
        public bool Used { get; set; }
        public string RequestedBy { get; set; } = string.Empty;
    }

    private sealed class CommandRecord
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
        public int DeliveryCount { get; set; }
        public string VerificationStatus { get; set; } = string.Empty;
        public DateTimeOffset? VerifiedUtc { get; set; }
        public string? VerificationMessage { get; set; }
        public string RequestedBy { get; set; } = string.Empty;
    }
}
