using OpsForge.Contracts;
using OpsForge.Server;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var rule = new CorrelationRule
{
    Id = "checkout",
    Title = "Checkout unavailable",
    Process = "Checkout.Api",
    ProbeIds = new() { "checkout-http", "checkout-tcp" },
    WindowSeconds = 30,
    MinimumFailures = 2
};
new CorrelationOptions { Rules = new() { rule } }.Validate();

var now = DateTimeOffset.UtcNow;
var heartbeat = new AgentHeartbeatRequest
{
    AgentId = "node-01",
    MachineName = "NODE01",
    TimestampUtc = now,
    MonitoredProcesses = new() { new ProcessMetric { Name = "Checkout.Api", Running = false } },
    Probes = new()
    {
        new ProbeMetric { Id = "checkout-http", Type = "HTTP", Target = "https://checkout/health", Success = false, Detail = "503", CheckedUtc = now },
        new ProbeMetric { Id = "checkout-tcp", Type = "TCP", Target = "checkout:443", Success = false, Detail = "connection refused", CheckedUtc = now }
    }
};

var evaluation = CorrelationEngine.Evaluate(heartbeat, rule);
Require(evaluation.Complete, "Expected complete correlation evidence.");
Require(evaluation.FailureCount == 3, "Expected three failed selectors.");
Require(evaluation.Candidate is not null, "Expected dry-run candidate.");
Require(evaluation.Candidate!.CorrelationKey == "node-01:primary:checkout", "Unexpected correlation key.");

var overlapping = new CorrelationRule
{
    Id = "other",
    Title = "Other",
    ProbeIds = new() { "checkout-http", "other-http" },
    WindowSeconds = 30,
    MinimumFailures = 2
};
var rejected = false;
try { new CorrelationOptions { Rules = new() { rule, overlapping } }.Validate(); }
catch (InvalidOperationException) { rejected = true; }
Require(rejected, "Overlapping selectors were not rejected.");

Console.WriteLine("PASS: correlation rule validation and dry-run checks");
