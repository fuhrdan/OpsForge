using OpsForge.Contracts;
using OpsForge.Server;

var time = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
var rule = new CorrelationRule
{
    Id = "payments", Title = "Payments unavailable", Process = "Payments.Worker",
    ProbeIds = new() { "payments-tcp", "payments-http" }, WindowSeconds = 30
};
var other = new CorrelationRule
{
    Id = "database", Title = "Database unavailable", Service = "Database",
    ProbeIds = new() { "database-tcp" }
};
new CorrelationOptions { Rules = new() { rule, other } }.Validate();

var heartbeat = new AgentHeartbeatRequest
{
    AgentId = "node-1", MachineName = "NODE-1", TimestampUtc = time,
    MonitoredProcesses = new() { new ProcessMetric { Name = "Payments.Worker", Running = false } },
    MonitoredServices = new() { new ServiceMetric { Name = "Database", Exists = true, Status = "Running" } },
    Probes = new()
    {
        new ProbeMetric { Id = "payments-tcp", Type = "TCP", Target = "payments:443", CheckedUtc = time.AddSeconds(-3), Success = false },
        new ProbeMetric { Id = "payments-http", Type = "HTTP", Target = "https://payments/health", CheckedUtc = time.AddSeconds(-5), Success = false },
        new ProbeMetric { Id = "database-tcp", Type = "TCP", Target = "database:5432", CheckedUtc = time, Success = false }
    }
};

var result = CorrelationEngine.Evaluate(heartbeat, rule);
Check(result.Candidate is not null && result.Candidate.Signals.Count == 3, "related failures correlate");
Check(result.Candidate!.CorrelationKey == "node-1:primary:payments", "stable correlation key");
Check(result.Candidate.Signals.Single(s => s.SignalType == "Process").Role == "Root-cause candidate", "root-cause role");
Check(CorrelationEngine.Evaluate(heartbeat, other).Candidate is null, "independent failure stays separate");

heartbeat.Probes.Add(new ProbeMetric { Id = "payments-tcp", Type = "TCP", Target = "payments:443", CheckedUtc = time.AddSeconds(-10), Success = true });
Check(CorrelationEngine.Evaluate(heartbeat, rule).Candidate?.Signals.Count == 3, "latest duplicate observation wins");

heartbeat.Probes.Reverse();
var reordered = CorrelationEngine.Evaluate(heartbeat, rule);
Check(result.Candidate.Signals.Select(s => s.SignalKey).SequenceEqual(reordered.Candidate!.Signals.Select(s => s.SignalKey)), "stable under reordered observations");

// The duplicate belongs only to the deduplication check; keep recovery cases unambiguous.
heartbeat.Probes.RemoveAll(p => p.Id == "payments-tcp" && p.CheckedUtc == time.AddSeconds(-10));

heartbeat.Probes.Single(p => p.Id == "payments-http").CheckedUtc = time.AddMinutes(-2);
var stale = CorrelationEngine.Evaluate(heartbeat, rule);
Check(stale.Candidate?.Signals.Count == 2 && !stale.Complete, "stale probe cannot contribute");

heartbeat.MonitoredProcesses[0].Running = true;
heartbeat.Probes.Single(p => p.Id == "payments-tcp").Success = true;
var missingRecovery = CorrelationEngine.Evaluate(heartbeat, rule);
Check(missingRecovery.Candidate is null && !missingRecovery.Complete, "missing data cannot prove recovery");

heartbeat.Probes.Single(p => p.Id == "payments-http").CheckedUtc = time;
heartbeat.Probes.Single(p => p.Id == "payments-http").Success = true;
var recovery = CorrelationEngine.Evaluate(heartbeat, rule);
Check(recovery.Candidate is null && recovery.Complete && recovery.FailureCount == 0, "complete healthy recovery");

heartbeat.MonitoredProcesses[0].Running = false;
heartbeat.Probes.Single(p => p.Id == "payments-http").Success = false;
heartbeat.Probes.Single(p => p.Id == "payments-http").CheckedUtc = time.AddMinutes(-2);
Check(CorrelationEngine.Evaluate(heartbeat, rule).Candidate is null, "failure outside window does not correlate");

try
{
    new CorrelationOptions { Rules = new() { rule, rule } }.Validate();
    throw new Exception("Duplicate rules were accepted.");
}
catch (InvalidOperationException) { }

var fleetRule = new CorrelationRule
{
    Id = "demo-application", Title = "Demo unavailable", Process = "OpsForge.DemoService",
    ProbeIds = new() { "demo-tcp", "demo-http" }, WindowSeconds = 30, MinimumFailures = 2
};
var root = FleetHeartbeat("source-01", "source", "lab-run-1", time, true);
var observer1 = FleetHeartbeat("observer-01", "observer", "lab-run-1", time.AddSeconds(2), true);
var observer2 = FleetHeartbeat("observer-02", "observer", "lab-run-1", time.AddSeconds(3), true);
var independent = FleetHeartbeat("observer-03", "observer", "lab-run-2", time.AddSeconds(3), true);
Check(FleetCorrelationEngine.Evaluate("lab-run-1", fleetRule,
    new[] { new FleetSnapshot(observer1, time.AddSeconds(2), "observer-first") }, null, time.AddSeconds(2)).Candidate is null,
    "an observer cannot open an incident without a source");
var observerBeforeSource = FleetCorrelationEngine.Evaluate("lab-run-1", fleetRule,
    new[] { new FleetSnapshot(observer1, time.AddSeconds(2), "observer-first"),
        new FleetSnapshot(FleetHeartbeat("source-01", "source", "lab-run-1", time, false), time, "healthy-source") },
    null, time.AddSeconds(2));
Check(observerBeforeSource.Candidate is null,
    "an observer sorting before a healthy source cannot open a fleet incident");
var first = FleetCorrelationEngine.Evaluate("lab-run-1", fleetRule,
    new[] { new FleetSnapshot(root, time, "root-trace") }, null, time);
Check(first.Candidate is { FleetEvidence.Count: 1 }, "source opens one fleet incident");
var firstIncident = first.Candidate!;
var all = new[]
{
    new FleetSnapshot(observer2, time.AddSeconds(3), "observer-trace-2"),
    new FleetSnapshot(independent, time.AddSeconds(3), "unrelated-trace"),
    new FleetSnapshot(root, time, "root-trace"),
    new FleetSnapshot(observer1, time.AddSeconds(2), "observer-trace-1")
};
var cascade = FleetCorrelationEngine.Evaluate("lab-run-1", fleetRule, all, firstIncident, time.AddSeconds(3));
Check(cascade.Candidate?.FleetEvidence.Count == 3 && cascade.Candidate.Signals.Count == 7,
    "one incident contains source and both observers");
Check(cascade.Candidate!.FleetEvidence.All(e => e.AgentId != independent.AgentId), "other service identity is separate");
Check(cascade.Candidate.FleetEvidence.Select(e => e.AgentId).SequenceEqual(new[] { "observer-01", "observer-02", "source-01" }),
    "fleet evidence order is deterministic");
Check(cascade.Candidate.FleetEvidence.Single(e => e.AgentId == "observer-01").TraceId == "observer-trace-1",
    "each observer keeps its own trace");

var recovering = new[]
{
    new FleetSnapshot(FleetHeartbeat("source-01", "source", "lab-run-1", time.AddSeconds(5), false), time.AddSeconds(5), "source-recovery"),
    new FleetSnapshot(FleetHeartbeat("observer-01", "observer", "lab-run-1", time.AddSeconds(5), false), time.AddSeconds(5), "observer-recovery"),
    new FleetSnapshot(observer2, time.AddSeconds(3), "observer-trace-2")
};
var partiallyRecovered = FleetCorrelationEngine.Evaluate("lab-run-1", fleetRule, recovering, cascade.Candidate, time.AddSeconds(5));
Check(partiallyRecovered.Candidate is not null && !partiallyRecovered.Recovered &&
      partiallyRecovered.Candidate.FleetEvidence.Count(e => e.Active) == 1, "partial recovery stays open");
Check(!FleetCorrelationEngine.Evaluate("lab-run-1", fleetRule, recovering.Take(2).ToArray(),
    partiallyRecovered.Candidate, time.AddSeconds(40)).Recovered, "missing observations do not close incident");
var allHealthy = recovering.Take(2).Append(new FleetSnapshot(
    FleetHeartbeat("observer-02", "observer", "lab-run-1", time.AddSeconds(6), false),
    time.AddSeconds(6), "observer-recovery-2")).ToArray();
var recoveredFleet = FleetCorrelationEngine.Evaluate("lab-run-1", fleetRule, allHealthy,
    partiallyRecovered.Candidate, time.AddSeconds(6));
Check(recoveredFleet.Recovered && recoveredFleet.Candidate!.FleetEvidence.All(e => !e.Active),
    "complete fresh recovery closes fleet incident");

Console.WriteLine("PASS: correlation rule checks");

static AgentHeartbeatRequest FleetHeartbeat(string id, string role, string service, DateTimeOffset timestamp, bool failed) => new()
{
    AgentId = id, MachineName = id, FleetRole = role, FleetServiceId = service,
    FleetRuleId = "demo-application", TimestampUtc = timestamp,
    MonitoredProcesses = role == "source"
        ? new() { new ProcessMetric { Name = "OpsForge.DemoService", Running = !failed } } : new(),
    Probes = new()
    {
        new ProbeMetric { Id = "demo-tcp", Type = "TCP", Target = "demo:5091", CheckedUtc = timestamp, Success = !failed },
        new ProbeMetric { Id = "demo-http", Type = "HTTP", Target = "http://demo/health", CheckedUtc = timestamp, Success = !failed }
    }
};

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception($"FAIL: {description}");
}
