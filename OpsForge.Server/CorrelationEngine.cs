using OpsForge.Contracts;

namespace OpsForge.Server;

public static class CorrelationEngine
{
    public static PrimaryIncidentDto? EvaluateDemoApplication(AgentHeartbeatRequest heartbeat, DateTimeOffset now)
    {
        var process = heartbeat.MonitoredProcesses.FirstOrDefault(p =>
            string.Equals(p.Name, "OpsForge.DemoService", StringComparison.OrdinalIgnoreCase));
        var http = heartbeat.Probes.FirstOrDefault(p =>
            string.Equals(p.Id, "demo-http", StringComparison.OrdinalIgnoreCase));
        var tcp = heartbeat.Probes.FirstOrDefault(p =>
            string.Equals(p.Id, "demo-tcp", StringComparison.OrdinalIgnoreCase));

        var processFailed = process is not null && !process.Running;
        var httpFailed = http is not null && !http.Success;
        var tcpFailed = tcp is not null && !tcp.Success;
        var failureCount = (processFailed ? 1 : 0) + (httpFailed ? 1 : 0) + (tcpFailed ? 1 : 0);

        // Correlate only when at least two independent observations agree.
        if (failureCount < 2)
        {
            return null;
        }

        var signals = new List<CorrelatedSignalDto>();
        if (processFailed && process is not null)
        {
            signals.Add(new CorrelatedSignalDto
            {
                SignalKey = $"{heartbeat.AgentId}:process:OpsForge.DemoService",
                SignalType = "Process",
                Target = "OpsForge.DemoService",
                State = "Failed",
                Role = "Root-cause candidate",
                Evidence = "The monitored OpsForge.DemoService process is not running."
            });
        }

        if (tcpFailed && tcp is not null)
        {
            signals.Add(new CorrelatedSignalDto
            {
                SignalKey = $"{heartbeat.AgentId}:probe:demo-tcp",
                SignalType = "TCP",
                Target = tcp.Target,
                State = "Failed",
                Role = processFailed ? "Supporting evidence" : "Root-cause candidate",
                Evidence = $"TCP listener check failed after {tcp.LatencyMs} ms. {tcp.Detail}"
            });
        }

        if (httpFailed && http is not null)
        {
            signals.Add(new CorrelatedSignalDto
            {
                SignalKey = $"{heartbeat.AgentId}:probe:demo-http",
                SignalType = "HTTP",
                Target = http.Target,
                State = "Failed",
                Role = "Impact evidence",
                Evidence = $"HTTP health check failed after {http.LatencyMs} ms. {http.Detail}"
            });
        }

        string rootCause;
        double confidenceScore;
        if (processFailed)
        {
            rootCause = "OpsForge.DemoService is not running; downstream TCP and HTTP failures are consistent with the stopped application process.";
            confidenceScore = failureCount == 3 ? 0.98 : 0.92;
        }
        else if (tcpFailed && httpFailed)
        {
            rootCause = "The application listener on TCP/5091 is unavailable while the process still appears present; inspect binding, startup state, or an internally hung process.";
            confidenceScore = 0.80;
        }
        else
        {
            rootCause = "Multiple application availability signals failed together, but the current telemetry does not isolate one component with high confidence.";
            confidenceScore = 0.70;
        }

        var confidence = confidenceScore >= 0.90 ? "High" : confidenceScore >= 0.75 ? "Medium" : "Low";
        var failedNames = string.Join(", ", signals.Select(s => s.SignalType));

        return new PrimaryIncidentDto
        {
            CorrelationKey = $"{heartbeat.AgentId}:primary:demo-application",
            AgentId = heartbeat.AgentId,
            Severity = "critical",
            Title = $"Demo application unavailable on {heartbeat.MachineName}",
            Summary = $"OpsForge correlated {failureCount} failing observations ({failedNames}) into one application outage instead of treating them as independent incidents.",
            ProbableRootCause = rootCause,
            BlastRadius = "Demo Web Application · HTTP /health · TCP/5091",
            Confidence = confidence,
            ConfidenceScore = confidenceScore,
            Signals = signals,
            LastSeenUtc = now,
            Active = true
        };
    }
}
