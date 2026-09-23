using OpsForge.Contracts;

namespace OpsForge.Server;

// Each rule explicitly names a dependency group; proximity alone never joins unrelated alerts.
public sealed class CorrelationRule
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Process { get; set; }
    public string? Service { get; set; }
    public List<string> ProbeIds { get; set; } = new();
    public int WindowSeconds { get; set; } = 30;
    public int MinimumFailures { get; set; } = 2;
}

public sealed class CorrelationOptions
{
    public List<CorrelationRule> Rules { get; set; } = new();

    public void Validate()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id) || rule.Id.Contains(':') || !ids.Add(rule.Id) ||
                string.IsNullOrWhiteSpace(rule.Title) || rule.WindowSeconds is < 1 or > 3600 ||
                rule.MinimumFailures < 2)
                throw new InvalidOperationException($"Invalid or duplicate correlation rule: {rule.Id}");

            var names = new List<string>();
            if (!string.IsNullOrWhiteSpace(rule.Process)) names.Add($"process:{rule.Process}");
            if (!string.IsNullOrWhiteSpace(rule.Service)) names.Add($"windows-service:{rule.Service}");
            names.AddRange(rule.ProbeIds.Select(id => $"probe:{id}"));
            if (names.Count < rule.MinimumFailures || names.Any(name => name.EndsWith(':')) ||
                names.Any(name => !selectors.Add(name)))
                throw new InvalidOperationException($"Rule {rule.Id} has missing, repeated, or overlapping signal selectors.");
        }
    }
}

public sealed record CorrelationEvaluation(PrimaryIncidentDto? Candidate, bool Complete, int FailureCount);

public static class CorrelationEngine
{
    public static CorrelationEvaluation Evaluate(AgentHeartbeatRequest heartbeat, CorrelationRule rule)
    {
        var observed = new List<(CorrelatedSignalDto Signal, DateTimeOffset Time, bool Failed)>();
        var eventTime = heartbeat.TimestampUtc;
        if (eventTime == default) return new(null, false, 0);
        var window = TimeSpan.FromSeconds(rule.WindowSeconds);

        if (!string.IsNullOrWhiteSpace(rule.Process))
        {
            var process = heartbeat.MonitoredProcesses
                .Where(p => string.Equals(p.Name, rule.Process, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.Running).ThenBy(p => p.ProcessId).FirstOrDefault();
            if (process is not null)
                observed.Add((Signal(heartbeat.AgentId, "process", process.Name, "Process", !process.Running,
                    $"Process {process.Name} is not running."), eventTime, !process.Running));
        }

        if (!string.IsNullOrWhiteSpace(rule.Service))
        {
            var service = heartbeat.MonitoredServices
                .Where(s => string.Equals(s.Name, rule.Service, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.Exists && string.Equals(s.Status, "Running", StringComparison.OrdinalIgnoreCase))
                .ThenBy(s => s.Status, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (service is not null)
            {
                var failed = !service.Exists || !string.Equals(service.Status, "Running", StringComparison.OrdinalIgnoreCase);
                observed.Add((Signal(heartbeat.AgentId, "windows-service", service.Name, "Service", failed,
                    $"Service {service.Name} reports {service.Status}."), eventTime, failed));
            }
        }

        foreach (var id in rule.ProbeIds)
        {
            var probe = heartbeat.Probes
                .Where(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.CheckedUtc).ThenBy(p => p.Success)
                .ThenBy(p => p.Target, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            // Delayed probes cannot prove either an outage or a recovery.
            if (probe is null || probe.CheckedUtc == default || (eventTime - probe.CheckedUtc).Duration() > window)
                continue;
            observed.Add((Signal(heartbeat.AgentId, "probe", probe.Id, probe.Type.ToUpperInvariant(), !probe.Success,
                $"{probe.Type} probe {probe.Target}: {probe.Detail}"), probe.CheckedUtc, !probe.Success));
        }

        var expected = rule.ProbeIds.Count + (string.IsNullOrWhiteSpace(rule.Process) ? 0 : 1) +
                       (string.IsNullOrWhiteSpace(rule.Service) ? 0 : 1);
        var complete = observed.Count == expected;
        var failures = observed.Where(item => item.Failed).ToList();
        if (failures.Count < rule.MinimumFailures ||
            failures.Max(item => item.Time) - failures.Min(item => item.Time) > window)
            return new(null, complete, failures.Count);

        failures.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Signal.SignalKey, b.Signal.SignalKey));
        var root = failures.FirstOrDefault(item => item.Signal.SignalType is "Process" or "Service");
        if (root.Signal is null) root = failures[0];
        foreach (var item in failures)
            item.Signal.Role = ReferenceEquals(item.Signal, root.Signal) ? "Root-cause candidate" : "Supporting evidence";

        var score = root.Signal.SignalType is "Process" or "Service" ? 0.90 : 0.75;
        if (failures.Count == expected) score = Math.Min(0.98, score + 0.05);
        return new(new PrimaryIncidentDto
        {
            CorrelationKey = $"{heartbeat.AgentId}:primary:{rule.Id}",
            AgentId = heartbeat.AgentId,
            Severity = "critical",
            Title = $"{rule.Title} on {heartbeat.MachineName}",
            Summary = $"{failures.Count} related failures within {rule.WindowSeconds} seconds: {string.Join(", ", failures.Select(item => item.Signal.SignalType))}.",
            ProbableRootCause = $"{root.Signal.SignalType} {root.Signal.Target} failed; other configured observations support this diagnosis. Verify the dependency before remediation.",
            BlastRadius = string.Join(" · ", failures.Select(item => item.Signal.Target).Distinct(StringComparer.OrdinalIgnoreCase)),
            Confidence = score >= 0.90 ? "High" : "Medium",
            ConfidenceScore = score,
            Signals = failures.Select(item => item.Signal).ToList(),
            LastSeenUtc = eventTime,
            Active = true
        }, complete, failures.Count);
    }

    private static CorrelatedSignalDto Signal(string agent, string kind, string target, string type, bool failed, string evidence) => new()
    {
        SignalKey = $"{agent}:{kind}:{target}",
        SignalType = type,
        Target = target,
        State = failed ? "Failed" : "Healthy",
        Evidence = evidence
    };
}
