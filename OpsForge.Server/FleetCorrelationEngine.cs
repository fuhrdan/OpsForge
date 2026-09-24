using OpsForge.Contracts;

namespace OpsForge.Server;

public sealed record FleetSnapshot(AgentHeartbeatRequest Heartbeat, DateTimeOffset ReceivedUtc, string TraceId);
public sealed record FleetCorrelationResult(PrimaryIncidentDto? Candidate, bool Recovered);

// Service identity is explicit in authenticated heartbeats. Time alone never joins unrelated agents.
public static class FleetCorrelationEngine
{
    public static FleetCorrelationResult Evaluate(string serviceId, CorrelationRule rule,
        IReadOnlyList<FleetSnapshot> snapshots, PrimaryIncidentDto? existing, DateTimeOffset now)
    {
        var fresh = snapshots.Where(s =>
                string.Equals(s.Heartbeat.FleetServiceId, serviceId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.Heartbeat.FleetRuleId, rule.Id, StringComparison.OrdinalIgnoreCase) &&
                now - s.ReceivedUtc <= TimeSpan.FromSeconds(Math.Max(20, rule.WindowSeconds)))
            .OrderBy(s => s.Heartbeat.AgentId, StringComparer.OrdinalIgnoreCase).ToList();
        var source = fresh.FirstOrDefault(s => string.Equals(s.Heartbeat.FleetRole, "source", StringComparison.OrdinalIgnoreCase));
        if (source is null) return new(null, false);

        var observerRule = new CorrelationRule
        {
            Id = rule.Id, Title = rule.Title, ProbeIds = rule.ProbeIds,
            WindowSeconds = rule.WindowSeconds,
            MinimumFailures = Math.Max(1, Math.Min(rule.MinimumFailures, rule.ProbeIds.Count))
        };
        var observations = fresh
            .Where(s => ReferenceEquals(s, source) ||
                (rule.ProbeIds.Count > 0 &&
                 string.Equals(s.Heartbeat.FleetRole, "observer", StringComparison.OrdinalIgnoreCase)))
            .Select(s => (Snapshot: s, Evaluation: CorrelationEngine.Evaluate(s.Heartbeat,
                ReferenceEquals(s, source) ? rule : observerRule))).ToList();
        var sourceFailure = observations.Single(o => ReferenceEquals(o.Snapshot, source)).Evaluation.Candidate;
        if (existing is null && sourceFailure is null) return new(null, false);

        var evidence = existing?.FleetEvidence.Select(CopyEvidence)
            .ToDictionary(e => e.AgentId, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, FleetEvidenceDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var (snapshot, evaluation) in observations)
        {
            var agentId = snapshot.Heartbeat.AgentId;
            var failure = evaluation.Candidate;
            if (failure is not null &&
                (existing is not null ||
                 (snapshot.Heartbeat.TimestampUtc - source.Heartbeat.TimestampUtc).Duration() <= TimeSpan.FromSeconds(rule.WindowSeconds)))
            {
                if (!evidence.TryGetValue(agentId, out var item))
                {
                    item = new FleetEvidenceDto
                    {
                        AgentId = agentId,
                        FirstSeenUtc = snapshot.ReceivedUtc,
                        TraceId = snapshot.TraceId
                    };
                    evidence.Add(agentId, item);
                }
                item.DisplayName = string.IsNullOrWhiteSpace(snapshot.Heartbeat.DisplayName)
                    ? snapshot.Heartbeat.MachineName : snapshot.Heartbeat.DisplayName;
                item.Role = snapshot.Heartbeat.FleetRole;
                item.LastSeenUtc = snapshot.ReceivedUtc;
                item.Active = true;
                item.RecoveryTraceId = string.Empty;
                item.Signals = failure.Signals;
            }
            else if (evidence.TryGetValue(agentId, out var item) &&
                     evaluation.Complete && evaluation.FailureCount == 0)
            {
                item.Active = false;
                item.LastSeenUtc = snapshot.ReceivedUtc;
                item.RecoveryTraceId = snapshot.TraceId;
            }
        }

        if (evidence.Count == 0) return new(null, false);
        var items = evidence.Values.OrderBy(e => e.AgentId, StringComparer.OrdinalIgnoreCase).ToList();
        var active = items.Where(e => e.Active).ToList();
        var recovered = existing is not null && active.Count == 0;
        var root = items.First(e => string.Equals(e.AgentId, source.Heartbeat.AgentId, StringComparison.OrdinalIgnoreCase));
        var rootSignals = root.Signals.Where(s => s.Role.Contains("Root-cause", StringComparison.OrdinalIgnoreCase)).ToList();
        var hasSourceFailure = root.Active;
        var confidence = hasSourceFailure && rootSignals.Any(s => s.SignalType is "Process" or "Service") ? 0.95 : 0.75;
        var candidate = new PrimaryIncidentDto
        {
            CorrelationKey = $"fleet:{serviceId}:{rule.Id}",
            FleetServiceId = serviceId,
            AgentId = source.Heartbeat.AgentId,
            TraceId = existing?.TraceId ?? root.TraceId,
            Title = $"{rule.Title} · {serviceId}",
            Severity = "critical",
            Summary = recovered
                ? "Fresh measurements confirm recovery on all affected agents."
                : $"{active.Count} affected agent(s) observed for service {serviceId}: {string.Join(", ", active.Select(e => e.DisplayName))}.",
            ProbableRootCause = rootSignals.Count > 0
                ? $"Source agent {root.DisplayName}: {string.Join(", ", rootSignals.Select(s => s.SignalType + " " + s.Target))}. Verify before remediation."
                : $"Source agent {root.DisplayName} has recovered; awaiting fresh recovery evidence from affected observers.",
            BlastRadius = string.Join(" · ", active.Select(e => e.DisplayName)),
            Confidence = confidence >= 0.90 ? "High" : "Medium",
            ConfidenceScore = confidence,
            Signals = items.SelectMany(e => e.Signals).OrderBy(s => s.SignalKey, StringComparer.OrdinalIgnoreCase).ToList(),
            FleetEvidence = items,
            Active = !recovered
        };
        return new(candidate, recovered);
    }

    private static FleetEvidenceDto CopyEvidence(FleetEvidenceDto evidence) => new()
    {
        AgentId = evidence.AgentId, DisplayName = evidence.DisplayName, Role = evidence.Role,
        TraceId = evidence.TraceId, RecoveryTraceId = evidence.RecoveryTraceId,
        FirstSeenUtc = evidence.FirstSeenUtc, LastSeenUtc = evidence.LastSeenUtc,
        Active = evidence.Active, Signals = evidence.Signals
    };
}
