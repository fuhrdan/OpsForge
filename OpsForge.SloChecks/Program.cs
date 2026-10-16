using OpsForge.Server;

static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
var cfg = new SloConfiguration { Enabled = true, EvaluationIntervalSeconds = 10 };
cfg.Definitions.Add(new SloDefinition
{
    Id = "checkout", Name = "Checkout", Enabled = true, ObjectivePercent = 99.9, ComplianceWindowHours = 720,
    AgentIds = { "app-01", "app-02", "app-01" },
    BurnRatePolicies = { new SloBurnRatePolicy { Id = "fast", ShortWindowMinutes = 5, LongWindowMinutes = 60, ShortBurnRateThreshold = 14.4, LongBurnRateThreshold = 14.4, Severity = "critical" } }
});
cfg.Validate();
Require(cfg.EvaluationIntervalSeconds == 30, "Minimum evaluation interval was not enforced.");
Require(cfg.Definitions[0].AgentIds.Count == 2, "Agent IDs were not deduplicated.");
var route = new NotificationRoute { Id = "slo-route", Enabled = true, MinimumSeverity = "warning", EventTypes = { "SloBurnRate", "SloRecovered" } };
var alert = new NotificationEnvelope { EventType = "SloBurnRate", IncidentId = "slo:checkout:fast", Severity = "critical", AgentId = "fleet" };
Require(route.Matches(alert), "SLO burn-rate event did not route through notification engine.");
alert.EventType = "Opened"; Require(!route.Matches(alert), "Unsubscribed event unexpectedly matched SLO-only route.");
Console.WriteLine("PASS: SLO configuration and burn-rate routing checks");
