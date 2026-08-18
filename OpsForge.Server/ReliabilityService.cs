using OpsForge.Contracts;

namespace OpsForge.Server;

public sealed class ReliabilityService
{
    private static readonly TimeSpan OfflineThreshold = TimeSpan.FromSeconds(20);
    private readonly SqliteRepository _repository;

    public ReliabilityService(SqliteRepository repository)
    {
        _repository = repository;
    }

    public ReliabilityDashboardDto GetDashboard(int requestedHours = 24, double requestedSlaTarget = 99.9)
    {
        var hours = Math.Clamp(requestedHours, 1, 24 * 30);
        var slaTarget = Math.Clamp(requestedSlaTarget, 90.0, 100.0);
        var now = DateTimeOffset.UtcNow;
        var start = now.AddHours(-hours);
        var sampleStart = start.Subtract(OfflineThreshold);
        var inventory = _repository.GetAgentInventory().Where(a => !a.Revoked).ToList();
        var samples = _repository.GetTelemetrySamples(sampleStart, now).ToList();
        var maintenance = _repository.GetMaintenanceWindows(start, now, includeCancelled: true).ToList();
        var incidents = _repository.GetPrimaryIncidentsSince(start).ToList();
        var agents = new List<AgentReliabilityDto>();

        double totalMonitored = 0;
        double totalUp = 0;
        double totalMaintenance = 0;

        foreach (var agent in inventory)
        {
            var first = agent.FirstSeenUtc ?? agent.EnrolledUtc;
            var effectiveStart = first > start ? first : start;
            var agentSamples = samples
                .Where(s => string.Equals(s.AgentId, agent.AgentId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.TimestampUtc)
                .ToList();
            var windows = RelevantMaintenance(maintenance, agent.AgentId, effectiveStart, now);
            var availability = ComputeAvailability(agentSamples, effectiveStart, now, windows);
            var inRangeSamples = agentSamples.Where(s => s.TimestampUtc >= effectiveStart && s.TimestampUtc <= now && !IsInsideMaintenance(s.TimestampUtc, windows)).ToList();
            var agentIncidents = incidents.Where(i => string.Equals(i.AgentId, agent.AgentId, StringComparison.OrdinalIgnoreCase)).ToList();
            var resolvedDurations = agentIncidents.Where(i => i.ResolvedUtc.HasValue).Select(i => (double)i.DurationSeconds).ToList();
            var probeTotal = inRangeSamples.Sum(s => s.ProbeTotal);
            var probeFailed = inRangeSamples.Sum(s => s.ProbeFailed);
            var latencySamples = inRangeSamples.Where(s => s.ProbeTotal > 0).ToList();

            agents.Add(new AgentReliabilityDto
            {
                AgentId = agent.AgentId,
                DisplayName = string.IsNullOrWhiteSpace(agent.DisplayName) ? agent.AgentId : agent.DisplayName,
                Site = agent.Site,
                EnvironmentName = agent.EnvironmentName,
                Online = agent.LastSeenUtc.HasValue && now - agent.LastSeenUtc.Value <= OfflineThreshold,
                AvailabilityPercent = availability.AvailabilityPercent,
                MonitoredSeconds = (long)Math.Round(availability.MonitoredSeconds),
                MaintenanceExcludedSeconds = (long)Math.Round(availability.MaintenanceSeconds),
                CpuAveragePercent = Average(inRangeSamples.Select(s => s.CpuPercent)),
                CpuPeakPercent = Max(inRangeSamples.Select(s => s.CpuPercent)),
                MemoryAveragePercent = Average(inRangeSamples.Select(s => s.MemoryUsedPercent)),
                MemoryPeakPercent = Max(inRangeSamples.Select(s => s.MemoryUsedPercent)),
                ProbeSuccessPercent = probeTotal == 0 ? 100.0 : Math.Clamp(100.0 * (probeTotal - probeFailed) / probeTotal, 0.0, 100.0),
                ProbeAverageLatencyMs = latencySamples.Count == 0 ? 0.0 : latencySamples.Average(s => s.ProbeAverageLatencyMs),
                IncidentsOpened = agentIncidents.Count(i => i.FirstSeenUtc >= start),
                AverageMttrSeconds = resolvedDurations.Count == 0 ? 0.0 : resolvedDurations.Average()
            });

            totalMonitored += availability.MonitoredSeconds;
            totalUp += availability.UpSeconds;
            totalMaintenance += availability.MaintenanceSeconds;
        }

        var fleetAvailability = totalMonitored <= 0.0 ? 100.0 : Math.Clamp(100.0 * totalUp / totalMonitored, 0.0, 100.0);
        var downtime = Math.Max(0.0, totalMonitored - totalUp);
        var allowedDowntime = totalMonitored * Math.Max(0.0, (100.0 - slaTarget) / 100.0);
        var errorBudgetRemaining = allowedDowntime <= 0.0
            ? (downtime <= 0.5 ? 100.0 : 0.0)
            : Math.Clamp(100.0 * (1.0 - (downtime / allowedDowntime)), 0.0, 100.0);
        var resolved = incidents.Where(i => i.ResolvedUtc.HasValue && i.ResolvedUtc.Value >= start).ToList();

        return new ReliabilityDashboardDto
        {
            GeneratedUtc = now,
            RangeHours = hours,
            SlaTargetPercent = slaTarget,
            FleetAvailabilityPercent = fleetAvailability,
            ErrorBudgetRemainingPercent = errorBudgetRemaining,
            MonitoredSeconds = (long)Math.Round(totalMonitored),
            DowntimeSeconds = (long)Math.Round(downtime),
            MaintenanceExcludedSeconds = (long)Math.Round(totalMaintenance),
            PrimaryIncidentsOpened = incidents.Count(i => i.FirstSeenUtc >= start),
            PrimaryIncidentsResolved = resolved.Count,
            AverageMttrSeconds = resolved.Count == 0 ? 0.0 : resolved.Average(i => (double)i.DurationSeconds),
            ActiveMaintenanceWindows = maintenance.Count(w => !w.Cancelled && w.StartUtc <= now && w.EndUtc > now),
            Agents = agents.OrderBy(a => a.AvailabilityPercent).ThenBy(a => a.DisplayName).ToList(),
            Timeline = BuildFleetTimeline(inventory, samples, maintenance, start, now, hours),
            IncidentTrend = BuildIncidentTrend(incidents, start, now, hours)
        };
    }

    public AgentHistoryDto GetAgentHistory(string agentId, int requestedHours = 24)
    {
        var hours = Math.Clamp(requestedHours, 1, 24 * 30);
        var now = DateTimeOffset.UtcNow;
        var start = now.AddHours(-hours);
        var samples = _repository.GetTelemetrySamples(start, now, agentId).ToList();
        var bucketCount = Math.Min(120, Math.Max(12, hours * 4));
        var bucketSeconds = Math.Max(60.0, (now - start).TotalSeconds / bucketCount);
        var points = new List<TelemetryHistoryPointDto>();

        for (var i = 0; i < bucketCount; i++)
        {
            var bucketStart = start.AddSeconds(i * bucketSeconds);
            var bucketEnd = i == bucketCount - 1 ? now.AddMilliseconds(1) : start.AddSeconds((i + 1) * bucketSeconds);
            var bucket = samples.Where(s => s.TimestampUtc >= bucketStart && s.TimestampUtc < bucketEnd).ToList();
            if (bucket.Count == 0) continue;
            var probeTotal = bucket.Sum(s => s.ProbeTotal);
            var probeFailed = bucket.Sum(s => s.ProbeFailed);
            var withProbes = bucket.Where(s => s.ProbeTotal > 0).ToList();
            points.Add(new TelemetryHistoryPointDto
            {
                TimestampUtc = bucketStart,
                CpuPercent = bucket.Average(s => s.CpuPercent),
                MemoryUsedPercent = bucket.Average(s => s.MemoryUsedPercent),
                ProbeTotal = probeTotal,
                ProbeFailed = probeFailed,
                ProbeSuccessPercent = probeTotal == 0 ? 100.0 : Math.Clamp(100.0 * (probeTotal - probeFailed) / probeTotal, 0.0, 100.0),
                ProbeAverageLatencyMs = withProbes.Count == 0 ? 0.0 : withProbes.Average(s => s.ProbeAverageLatencyMs)
            });
        }

        return new AgentHistoryDto { AgentId = agentId, RangeHours = hours, Points = points };
    }

    private List<ReliabilityPointDto> BuildFleetTimeline(
        IReadOnlyList<AgentInventoryDto> inventory,
        IReadOnlyList<TelemetrySampleRecord> samples,
        IReadOnlyList<MaintenanceWindowDto> maintenance,
        DateTimeOffset start,
        DateTimeOffset now,
        int hours)
    {
        var bucketCount = Math.Min(96, Math.Max(12, hours * 2));
        var bucketSeconds = Math.Max(60.0, (now - start).TotalSeconds / bucketCount);
        var points = new List<ReliabilityPointDto>();

        for (var i = 0; i < bucketCount; i++)
        {
            var bucketStart = start.AddSeconds(i * bucketSeconds);
            var bucketEnd = i == bucketCount - 1 ? now : start.AddSeconds((i + 1) * bucketSeconds);
            var bucketSamples = samples.Where(s => s.TimestampUtc >= bucketStart && s.TimestampUtc < bucketEnd).ToList();
            var availabilityTotals = new List<AvailabilityResult>();
            foreach (var agent in inventory)
            {
                var first = agent.FirstSeenUtc ?? agent.EnrolledUtc;
                var effectiveStart = first > bucketStart ? first : bucketStart;
                if (effectiveStart >= bucketEnd) continue;
                var agentSamples = samples.Where(s => string.Equals(s.AgentId, agent.AgentId, StringComparison.OrdinalIgnoreCase) && s.TimestampUtc >= bucketStart.Subtract(OfflineThreshold) && s.TimestampUtc <= bucketEnd).OrderBy(s => s.TimestampUtc).ToList();
                var windows = RelevantMaintenance(maintenance, agent.AgentId, effectiveStart, bucketEnd);
                availabilityTotals.Add(ComputeAvailability(agentSamples, effectiveStart, bucketEnd, windows));
            }
            var monitored = availabilityTotals.Sum(a => a.MonitoredSeconds);
            var up = availabilityTotals.Sum(a => a.UpSeconds);
            var probeTotal = bucketSamples.Sum(s => s.ProbeTotal);
            var probeFailed = bucketSamples.Sum(s => s.ProbeFailed);
            var withProbes = bucketSamples.Where(s => s.ProbeTotal > 0).ToList();
            points.Add(new ReliabilityPointDto
            {
                TimestampUtc = bucketStart,
                CpuAveragePercent = Average(bucketSamples.Select(s => s.CpuPercent)),
                MemoryAveragePercent = Average(bucketSamples.Select(s => s.MemoryUsedPercent)),
                AvailabilityPercent = monitored <= 0.0 ? 100.0 : Math.Clamp(100.0 * up / monitored, 0.0, 100.0),
                ProbeSuccessPercent = probeTotal == 0 ? 100.0 : Math.Clamp(100.0 * (probeTotal - probeFailed) / probeTotal, 0.0, 100.0),
                ProbeAverageLatencyMs = withProbes.Count == 0 ? 0.0 : withProbes.Average(s => s.ProbeAverageLatencyMs)
            });
        }
        return points;
    }

    private static List<IncidentTrendPointDto> BuildIncidentTrend(IReadOnlyList<PrimaryIncidentDto> incidents, DateTimeOffset start, DateTimeOffset now, int hours)
    {
        var bucketHours = hours <= 48 ? 6 : hours <= 24 * 14 ? 24 : 72;
        var points = new List<IncidentTrendPointDto>();
        for (var cursor = start; cursor < now; cursor = cursor.AddHours(bucketHours))
        {
            var end = cursor.AddHours(bucketHours) > now ? now.AddMilliseconds(1) : cursor.AddHours(bucketHours);
            var opened = incidents.Where(i => i.FirstSeenUtc >= cursor && i.FirstSeenUtc < end).ToList();
            var resolved = incidents.Where(i => i.ResolvedUtc.HasValue && i.ResolvedUtc.Value >= cursor && i.ResolvedUtc.Value < end).ToList();
            points.Add(new IncidentTrendPointDto
            {
                BucketStartUtc = cursor,
                Opened = opened.Count,
                Resolved = resolved.Count,
                AverageMttrSeconds = resolved.Count == 0 ? 0.0 : resolved.Average(i => (double)i.DurationSeconds)
            });
        }
        return points;
    }

    private static AvailabilityResult ComputeAvailability(
        IReadOnlyList<TelemetrySampleRecord> samples,
        DateTimeOffset start,
        DateTimeOffset end,
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> maintenance)
    {
        if (end <= start) return new AvailabilityResult();
        var mergedMaintenance = MergeIntervals(maintenance, start, end);
        var maintenanceSeconds = mergedMaintenance.Sum(i => Math.Max(0.0, (i.End - i.Start).TotalSeconds));
        var monitoredSeconds = Math.Max(0.0, (end - start).TotalSeconds - maintenanceSeconds);
        if (monitoredSeconds <= 0.0) return new AvailabilityResult { MaintenanceSeconds = maintenanceSeconds };

        var ordered = samples.Where(s => s.TimestampUtc <= end).OrderBy(s => s.TimestampUtc).ToList();
        var up = 0.0;
        DateTimeOffset? prior = ordered.Where(s => s.TimestampUtc <= start).Select(s => (DateTimeOffset?)s.TimestampUtc).LastOrDefault();
        var inRange = ordered.Where(s => s.TimestampUtc > start && s.TimestampUtc <= end).ToList();
        var cursor = start;

        foreach (var sample in inRange)
        {
            var last = prior;
            if (last.HasValue)
            {
                var upUntil = last.Value.Add(OfflineThreshold);
                var segmentUpEnd = upUntil < sample.TimestampUtc ? upUntil : sample.TimestampUtc;
                if (segmentUpEnd > cursor) up += EffectiveSeconds(cursor, segmentUpEnd, mergedMaintenance);
            }
            cursor = sample.TimestampUtc;
            prior = sample.TimestampUtc;
        }

        if (prior.HasValue)
        {
            var upUntil = prior.Value.Add(OfflineThreshold);
            var segmentUpEnd = upUntil < end ? upUntil : end;
            if (segmentUpEnd > cursor) up += EffectiveSeconds(cursor, segmentUpEnd, mergedMaintenance);
        }

        return new AvailabilityResult
        {
            MonitoredSeconds = monitoredSeconds,
            UpSeconds = Math.Clamp(up, 0.0, monitoredSeconds),
            MaintenanceSeconds = maintenanceSeconds
        };
    }

    private static List<(DateTimeOffset Start, DateTimeOffset End)> RelevantMaintenance(
        IReadOnlyList<MaintenanceWindowDto> windows,
        string agentId,
        DateTimeOffset start,
        DateTimeOffset end) => windows
            .Where(w => w.AgentId == "*" || string.Equals(w.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
            .Select(w =>
            {
                var effectiveEnd = w.Cancelled && w.CancelledUtc.HasValue && w.CancelledUtc.Value < w.EndUtc
                    ? w.CancelledUtc.Value
                    : w.EndUtc;
                return (Window: w, EffectiveEnd: effectiveEnd);
            })
            .Where(x => x.EffectiveEnd > x.Window.StartUtc)
            .Where(x => x.EffectiveEnd > start && x.Window.StartUtc < end)
            .Select(x => (x.Window.StartUtc < start ? start : x.Window.StartUtc, x.EffectiveEnd > end ? end : x.EffectiveEnd))
            .ToList();

    private static bool IsInsideMaintenance(DateTimeOffset timestamp, IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> windows) =>
        windows.Any(w => timestamp >= w.Start && timestamp < w.End);

    private static List<(DateTimeOffset Start, DateTimeOffset End)> MergeIntervals(
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> intervals,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        var ordered = intervals
            .Select(i => (Start: i.Start < start ? start : i.Start, End: i.End > end ? end : i.End))
            .Where(i => i.End > i.Start)
            .OrderBy(i => i.Start)
            .ToList();
        var merged = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var current in ordered)
        {
            if (merged.Count == 0 || current.Start > merged[^1].End)
            {
                merged.Add(current);
                continue;
            }
            var last = merged[^1];
            if (current.End > last.End) merged[^1] = (last.Start, current.End);
        }
        return merged;
    }

    private static double EffectiveSeconds(DateTimeOffset start, DateTimeOffset end, IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> maintenance)
    {
        if (end <= start) return 0.0;
        var seconds = (end - start).TotalSeconds;
        foreach (var window in maintenance)
        {
            var overlapStart = window.Start > start ? window.Start : start;
            var overlapEnd = window.End < end ? window.End : end;
            if (overlapEnd > overlapStart) seconds -= (overlapEnd - overlapStart).TotalSeconds;
        }
        return Math.Max(0.0, seconds);
    }

    private static double Average(IEnumerable<double> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? 0.0 : list.Average();
    }

    private static double Max(IEnumerable<double> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? 0.0 : list.Max();
    }

    private sealed class AvailabilityResult
    {
        public double MonitoredSeconds { get; set; }
        public double UpSeconds { get; set; }
        public double MaintenanceSeconds { get; set; }
        public double AvailabilityPercent => MonitoredSeconds <= 0.0 ? 100.0 : Math.Clamp(100.0 * UpSeconds / MonitoredSeconds, 0.0, 100.0);
    }
}
