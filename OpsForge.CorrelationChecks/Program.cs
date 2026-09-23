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

Console.WriteLine("PASS: correlation rule checks");

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception($"FAIL: {description}");
}
