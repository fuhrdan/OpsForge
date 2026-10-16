namespace OpsForge.Server;

public sealed class SloService
{
    private static readonly TimeSpan OfflineThreshold = TimeSpan.FromSeconds(20);
    private readonly SqliteRepository _repository;
    private readonly SloConfiguration _configuration;

    public SloService(SqliteRepository repository, SloConfiguration configuration)
    {
        _repository = repository;
        _configuration = configuration;
    }

    public IReadOnlyList<SloEvaluationDto> GetEvaluations()
    {
        if (!_configuration.Enabled) return Array.Empty<SloEvaluationDto>();
        var now = DateTimeOffset.UtcNow;
        return _configuration.Definitions.Where(d => d.Enabled).Select(d => Evaluate(d, now)).OrderBy(e => e.Status).ThenBy(e => e.Name).ToList();
    }

    public SloEvaluationDto? GetEvaluation(string id)
    {
        if (!_configuration.Enabled) return null;
        var definition = _configuration.Definitions.FirstOrDefault(d => d.Enabled && string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
        return definition is null ? null : Evaluate(definition, DateTimeOffset.UtcNow);
    }

    public SloEvaluationDto Evaluate(SloDefinition definition, DateTimeOffset now)
    {
        var compliance = Measure(definition, TimeSpan.FromHours(definition.ComplianceWindowHours), now);
        var allowed = Math.Max(0.0000001, 1.0 - definition.ObjectivePercent / 100.0);
        var consumed = compliance.HasData ? Math.Max(0.0, 100.0 * compliance.ErrorFraction / allowed) : 0.0;
        var burns = definition.BurnRatePolicies.Select(policy =>
        {
            var shortWindow = Measure(definition, TimeSpan.FromMinutes(policy.ShortWindowMinutes), now);
            var longWindow = Measure(definition, TimeSpan.FromMinutes(policy.LongWindowMinutes), now);
            return new SloBurnRateEvaluation
            {
                PolicyId = policy.Id, Severity = policy.Severity, ShortWindow = shortWindow, LongWindow = longWindow,
                ShortThreshold = policy.ShortBurnRateThreshold, LongThreshold = policy.LongBurnRateThreshold,
                Alerting = shortWindow.HasData && longWindow.HasData && shortWindow.BurnRate >= policy.ShortBurnRateThreshold && longWindow.BurnRate >= policy.LongBurnRateThreshold
            };
        }).ToList();
        var status = !compliance.HasData ? "no-data" : burns.Any(b => b.Alerting) ? "burning" : consumed >= 100.0 ? "budget-exhausted" : "healthy";
        return new SloEvaluationDto
        {
            Id = definition.Id, Name = definition.Name, ObjectivePercent = definition.ObjectivePercent,
            ComplianceWindowHours = definition.ComplianceWindowHours, AgentIds = definition.AgentIds,
            EvaluatedUtc = now, ComplianceWindow = compliance, ErrorBudgetConsumedPercent = consumed,
            ErrorBudgetRemainingPercent = Math.Clamp(100.0 - consumed, 0.0, 100.0), Status = status, BurnRates = burns
        };
    }

    private SloWindowMeasurement Measure(SloDefinition definition, TimeSpan window, DateTimeOffset now)
    {
        var start = now.Subtract(window);
        var inventory = _repository.GetAgentInventory().Where(a => !a.Revoked)
            .Where(a => definition.AgentIds.Count == 0 || definition.AgentIds.Any(id => string.Equals(id, a.AgentId, StringComparison.OrdinalIgnoreCase))).ToList();
        if (inventory.Count == 0) return Empty(window);
        var samples = _repository.GetTelemetrySamples(start.Subtract(OfflineThreshold), now).ToList();
        var maintenance = _repository.GetMaintenanceWindows(start, now, includeCancelled: true).ToList();
        double monitored = 0.0, up = 0.0;
        foreach (var agent in inventory)
        {
            var first = agent.FirstSeenUtc ?? agent.EnrolledUtc;
            var effectiveStart = first > start ? first : start;
            if (effectiveStart >= now) continue;
            var agentSamples = samples.Where(s => string.Equals(s.AgentId, agent.AgentId, StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.TimestampUtc).ToList();
            var windows = RelevantMaintenance(maintenance, agent.AgentId, effectiveStart, now);
            var result = ComputeAvailability(agentSamples, effectiveStart, now, windows);
            monitored += result.MonitoredSeconds; up += result.UpSeconds;
        }
        if (monitored <= 0.0) return Empty(window);
        var downtime = Math.Max(0.0, monitored - up);
        var error = Math.Clamp(downtime / monitored, 0.0, 1.0);
        var allowed = Math.Max(0.0000001, 1.0 - definition.ObjectivePercent / 100.0);
        return new SloWindowMeasurement
        {
            WindowMinutes = Math.Max(1, (int)Math.Round(window.TotalMinutes)), MonitoredSeconds = (long)Math.Round(monitored),
            DowntimeSeconds = (long)Math.Round(downtime), AvailabilityPercent = Math.Clamp(100.0 * up / monitored, 0.0, 100.0),
            ErrorFraction = error, AllowedErrorFraction = allowed, BurnRate = error / allowed, HasData = true
        };
    }

    private static SloWindowMeasurement Empty(TimeSpan window) => new() { WindowMinutes = Math.Max(1, (int)Math.Round(window.TotalMinutes)), AvailabilityPercent = 100.0, HasData = false };

    private static AvailabilityResult ComputeAvailability(IReadOnlyList<TelemetrySampleRecord> samples, DateTimeOffset start, DateTimeOffset end, IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> maintenance)
    {
        if (end <= start) return new AvailabilityResult();
        var merged = MergeIntervals(maintenance, start, end);
        var maintenanceSeconds = merged.Sum(i => Math.Max(0.0, (i.End - i.Start).TotalSeconds));
        var monitored = Math.Max(0.0, (end - start).TotalSeconds - maintenanceSeconds);
        if (monitored <= 0.0) return new AvailabilityResult();
        var ordered = samples.Where(s => s.TimestampUtc <= end).OrderBy(s => s.TimestampUtc).ToList();
        var up = 0.0; DateTimeOffset? prior = ordered.Where(s => s.TimestampUtc <= start).Select(s => (DateTimeOffset?)s.TimestampUtc).LastOrDefault();
        var cursor = start;
        foreach (var sample in ordered.Where(s => s.TimestampUtc > start && s.TimestampUtc <= end))
        {
            if (prior.HasValue)
            {
                var upUntil = prior.Value.Add(OfflineThreshold);
                var segmentEnd = upUntil < sample.TimestampUtc ? upUntil : sample.TimestampUtc;
                if (segmentEnd > cursor) up += EffectiveSeconds(cursor, segmentEnd, merged);
            }
            cursor = sample.TimestampUtc; prior = sample.TimestampUtc;
        }
        if (prior.HasValue)
        {
            var upUntil = prior.Value.Add(OfflineThreshold);
            var segmentEnd = upUntil < end ? upUntil : end;
            if (segmentEnd > cursor) up += EffectiveSeconds(cursor, segmentEnd, merged);
        }
        return new AvailabilityResult { MonitoredSeconds = monitored, UpSeconds = Math.Clamp(up, 0.0, monitored) };
    }

    private static List<(DateTimeOffset Start, DateTimeOffset End)> RelevantMaintenance(IReadOnlyList<OpsForge.Contracts.MaintenanceWindowDto> windows, string agentId, DateTimeOffset start, DateTimeOffset end) => windows
        .Where(w => w.AgentId == "*" || string.Equals(w.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
        .Select(w => (Window: w, EffectiveEnd: w.Cancelled && w.CancelledUtc.HasValue && w.CancelledUtc.Value < w.EndUtc ? w.CancelledUtc.Value : w.EndUtc))
        .Where(x => x.EffectiveEnd > x.Window.StartUtc && x.EffectiveEnd > start && x.Window.StartUtc < end)
        .Select(x => (x.Window.StartUtc < start ? start : x.Window.StartUtc, x.EffectiveEnd > end ? end : x.EffectiveEnd)).ToList();

    private static List<(DateTimeOffset Start, DateTimeOffset End)> MergeIntervals(IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> intervals, DateTimeOffset start, DateTimeOffset end)
    {
        var ordered = intervals.Select(i => (Start: i.Start < start ? start : i.Start, End: i.End > end ? end : i.End)).Where(i => i.End > i.Start).OrderBy(i => i.Start).ToList();
        var merged = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var current in ordered)
        {
            if (merged.Count == 0 || current.Start > merged[^1].End) { merged.Add(current); continue; }
            var last = merged[^1]; if (current.End > last.End) merged[^1] = (last.Start, current.End);
        }
        return merged;
    }

    private static double EffectiveSeconds(DateTimeOffset start, DateTimeOffset end, IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> maintenance)
    {
        var seconds = Math.Max(0.0, (end - start).TotalSeconds);
        foreach (var w in maintenance)
        {
            var a = w.Start > start ? w.Start : start; var b = w.End < end ? w.End : end;
            if (b > a) seconds -= (b - a).TotalSeconds;
        }
        return Math.Max(0.0, seconds);
    }

    private sealed class AvailabilityResult { public double MonitoredSeconds { get; set; } public double UpSeconds { get; set; } }
}
