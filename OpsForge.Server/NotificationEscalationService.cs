namespace OpsForge.Server;

public sealed class NotificationEscalationService : BackgroundService
{
    private readonly TelemetryStore _telemetry;
    private readonly NotificationQueue _queue;
    private readonly NotificationConfiguration _configuration;
    private readonly NotificationStore _store;
    private readonly ILogger<NotificationEscalationService> _logger;

    public NotificationEscalationService(
        TelemetryStore telemetry,
        NotificationQueue queue,
        NotificationConfiguration configuration,
        NotificationStore store,
        ILogger<NotificationEscalationService> logger)
    {
        _telemetry = telemetry;
        _queue = queue;
        _configuration = configuration;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!_configuration.Enabled)
                continue;

            try
            {
                var now = DateTimeOffset.UtcNow;
                var threshold = TimeSpan.FromSeconds(_configuration.EscalationSeconds);

                foreach (var incident in _telemetry.GetPrimaryIncidents()
                    .Where(i => i.Active)
                    .Where(i => !i.Acknowledged)
                    .Where(i => !i.MaintenanceSuppressed)
                    .Where(i => now - i.FirstSeenUtc >= threshold))
                {
                    var routes = _configuration.Routes
                        .Where(route => route.Enabled)
                        .Where(route => route.EventTypes.Any(value =>
                            string.Equals(value, "Escalated", StringComparison.OrdinalIgnoreCase)))
                        .ToList();

                    if (routes.Count == 0)
                        continue;

                    if (routes.All(route =>
                        _store.HasAnySuccessfulDelivery(route.Id, "Escalated", incident.Id)))
                        continue;

                    _queue.Enqueue(NotificationEnvelope.FromPrimary(
                        "Escalated", incident, now, "OpsForge escalation timer"));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OpsForge notification escalation scan failed.");
            }
        }
    }
}
