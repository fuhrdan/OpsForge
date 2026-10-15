using OpsForge.Server;

var failures = new List<string>();

Check(NotificationRoute.SeverityRank("critical") > NotificationRoute.SeverityRank("warning"),
    "critical ranks above warning");
Check(NotificationRoute.SeverityRank("warning") > NotificationRoute.SeverityRank("info"),
    "warning ranks above info");

var route = new NotificationRoute
{
    Id = "test",
    Enabled = true,
    ChannelType = "webhook",
    TargetEnvironmentVariable = "OPSFORGE_TEST_WEBHOOK",
    EventTypes = new() { "Opened", "Resolved" },
    MinimumSeverity = "warning",
    SuppressDuringMaintenance = true
};

var envelope = new NotificationEnvelope
{
    EventType = "Opened",
    IncidentId = "inc-1",
    AgentId = "node-1",
    Severity = "critical",
    Title = "Demo outage",
    OccurredUtc = DateTimeOffset.UtcNow
};

Check(route.Matches(envelope), "critical opened incident matches warning-or-higher route");
envelope.Severity = "info";
Check(!route.Matches(envelope), "info incident does not match warning route");
envelope.Severity = "critical";
envelope.EventType = "Assigned";
Check(!route.Matches(envelope), "unlisted event type does not match");
envelope.EventType = "Opened";
envelope.MaintenanceSuppressed = true;
Check(!route.Matches(envelope), "maintenance-suppressed incident is excluded");
route.SuppressDuringMaintenance = false;
Check(route.Matches(envelope), "route can explicitly include maintenance incidents");

if (failures.Count > 0)
{
    foreach (var failure in failures)
        Console.Error.WriteLine($"FAIL: {failure}");
    Environment.Exit(1);
}

Console.WriteLine("PASS: notification routing checks");

void Check(bool condition, string description)
{
    if (!condition) failures.Add(description);
}
