namespace OpsForge.Server;

public sealed class SloBurnRateMonitor : BackgroundService
{
    private readonly SloConfiguration _configuration;
    private readonly SloService _sloService;
    private readonly NotificationQueue _notificationQueue;
    private readonly ILogger<SloBurnRateMonitor> _logger;
    private readonly Dictionary<string, bool> _active = new(StringComparer.OrdinalIgnoreCase);

    public SloBurnRateMonitor(SloConfiguration configuration, SloService sloService, NotificationQueue notificationQueue, ILogger<SloBurnRateMonitor> logger)
    {
        _configuration = configuration; _sloService = sloService; _notificationQueue = notificationQueue; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.Enabled) { _logger.LogInformation("SLO burn-rate monitoring is disabled."); return; }
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { Evaluate(); } catch (Exception ex) { _logger.LogError(ex, "SLO burn-rate evaluation failed."); }
            await Task.Delay(TimeSpan.FromSeconds(_configuration.EvaluationIntervalSeconds), stoppingToken);
        }
    }

    private void Evaluate()
    {
        foreach (var evaluation in _sloService.GetEvaluations())
        foreach (var burn in evaluation.BurnRates)
        {
            var key = $"{evaluation.Id}:{burn.PolicyId}"; _active.TryGetValue(key, out var wasActive);
            if (burn.Alerting && !wasActive) { _notificationQueue.Enqueue(CreateEnvelope("SloBurnRate", evaluation, burn)); _active[key] = true; }
            else if (!burn.Alerting && wasActive) { _notificationQueue.Enqueue(CreateEnvelope("SloRecovered", evaluation, burn)); _active[key] = false; }
        }
    }

    private static NotificationEnvelope CreateEnvelope(string eventType, SloEvaluationDto evaluation, SloBurnRateEvaluation burn)
    {
        var recovery = string.Equals(eventType, "SloRecovered", StringComparison.OrdinalIgnoreCase);
        var scope = evaluation.AgentIds.Count == 0 ? "fleet" : string.Join(",", evaluation.AgentIds);
        return new NotificationEnvelope
        {
            EventType = eventType, IncidentId = $"slo:{evaluation.Id}:{burn.PolicyId}", CorrelationKey = $"slo:{evaluation.Id}",
            AgentId = evaluation.AgentIds.Count == 1 ? evaluation.AgentIds[0] : "fleet", Severity = recovery ? "info" : burn.Severity,
            Title = recovery ? $"SLO recovered: {evaluation.Name}" : $"SLO burn-rate alert: {evaluation.Name}",
            Summary = $"Objective {evaluation.ObjectivePercent:0.###}% · scope {scope} · short {burn.ShortWindow.WindowMinutes}m {burn.ShortWindow.BurnRate:0.##}x/{burn.ShortThreshold:0.##}x · long {burn.LongWindow.WindowMinutes}m {burn.LongWindow.BurnRate:0.##}x/{burn.LongThreshold:0.##}x · budget remaining {evaluation.ErrorBudgetRemainingPercent:0.##}%.",
            ProbableRootCause = recovery ? "Observed error consumption returned below the configured multi-window threshold." : "Observed error consumption exceeds the configured multi-window SLO burn-rate thresholds.",
            BlastRadius = $"SLO scope: {scope}.", Actor = "slo-monitor", OccurredUtc = DateTimeOffset.UtcNow
        };
    }
}
