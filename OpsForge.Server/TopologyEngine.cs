using OpsForge.Contracts;

namespace OpsForge.Server;

public static class TopologyEngine
{
    public static TopologySnapshotDto Build(
        IReadOnlyList<AgentSnapshotDto> agents,
        IReadOnlyList<IncidentDto> incidents)
    {
        var snapshot = new TopologySnapshotDto
        {
            GeneratedUtc = DateTimeOffset.UtcNow,
            AgentsTotal = agents.Count,
            AgentsOnline = agents.Count(a => a.Online),
            SuppressedSignalCount = incidents.Count(i => i.Active && i.Suppressed)
        };

        foreach (var agent in agents)
        {
            var h = agent.Heartbeat;
            var hostId = HostId(h.AgentId);
            snapshot.Nodes.Add(new TopologyNodeDto
            {
                Id = hostId,
                AgentId = h.AgentId,
                Kind = "host",
                Label = string.IsNullOrWhiteSpace(h.DisplayName) ? h.MachineName : h.DisplayName,
                Detail = $"{h.MachineName} · {h.OperatingSystem}",
                Health = agent.Online ? "healthy" : "failed",
                Site = h.Site,
                EnvironmentName = h.EnvironmentName
            });

            foreach (var process in h.MonitoredProcesses)
            {
                var id = ProcessId(h.AgentId, process.Name);
                snapshot.Nodes.Add(Node(id, h, "process", process.Name,
                    process.Running ? $"Running · PID {process.ProcessId}" : "Not running",
                    process.Running));
                snapshot.Edges.Add(Edge(hostId, id, "contains", "runs", false));
            }

            foreach (var service in h.MonitoredServices)
            {
                var id = ServiceId(h.AgentId, service.Name);
                var healthy = service.Exists && string.Equals(service.Status, "Running", StringComparison.OrdinalIgnoreCase);
                snapshot.Nodes.Add(Node(id, h, "windows-service", service.DisplayName, service.Status, healthy));
                snapshot.Edges.Add(Edge(hostId, id, "contains", "runs", false));
            }

            foreach (var probe in h.Probes)
            {
                var id = ProbeId(h.AgentId, probe.Id);
                snapshot.Nodes.Add(Node(id, h, "probe", $"{probe.Type} · {probe.Id}",
                    $"{probe.Target} · {probe.LatencyMs} ms", probe.Success));
                snapshot.Edges.Add(Edge(hostId, id, "observes", "checks", false));
            }

            AddLocalDemoDependencies(snapshot, h);
        }

        AddCrossAgentDependencies(snapshot, agents);
        snapshot.HealthyNodes = snapshot.Nodes.Count(n => n.Health == "healthy");
        snapshot.FailedNodes = snapshot.Nodes.Count(n => n.Health == "failed");
        snapshot.Impacts = BuildImpacts(snapshot);
        return snapshot;
    }

    public static string ExpandBlastRadius(string agentId, string baseBlastRadius, IReadOnlyList<AgentSnapshotDto> agents)
    {
        var target = agents.FirstOrDefault(a => string.Equals(a.Heartbeat.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
        if (target is null) return baseBlastRadius;

        var aliases = Aliases(target.Heartbeat).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var affected = new List<string>();
        foreach (var observer in agents.Where(a => !string.Equals(a.Heartbeat.AgentId, agentId, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var probe in observer.Heartbeat.Probes)
            {
                var host = ProbeHost(probe);
                if (!string.IsNullOrWhiteSpace(host) && aliases.Contains(host))
                {
                    var name = string.IsNullOrWhiteSpace(observer.Heartbeat.DisplayName)
                        ? observer.Heartbeat.MachineName : observer.Heartbeat.DisplayName;
                    affected.Add($"{name}:{probe.Id}");
                }
            }
        }

        return affected.Count == 0
            ? baseBlastRadius
            : $"{baseBlastRadius} · Downstream observers: {string.Join(", ", affected.Distinct(StringComparer.OrdinalIgnoreCase))}";
    }

    private static void AddLocalDemoDependencies(TopologySnapshotDto snapshot, AgentHeartbeatRequest h)
    {
        var process = h.MonitoredProcesses.FirstOrDefault(p => string.Equals(p.Name, "OpsForge.DemoService", StringComparison.OrdinalIgnoreCase));
        var tcp = h.Probes.FirstOrDefault(p => string.Equals(p.Id, "demo-tcp", StringComparison.OrdinalIgnoreCase));
        var http = h.Probes.FirstOrDefault(p => string.Equals(p.Id, "demo-http", StringComparison.OrdinalIgnoreCase));
        if (process is not null && tcp is not null)
            snapshot.Edges.Add(Edge(ProcessId(h.AgentId, process.Name), ProbeId(h.AgentId, tcp.Id), "dependency", "opens listener", false));
        if (tcp is not null && http is not null)
            snapshot.Edges.Add(Edge(ProbeId(h.AgentId, tcp.Id), ProbeId(h.AgentId, http.Id), "dependency", "serves", false));
    }

    private static void AddCrossAgentDependencies(TopologySnapshotDto snapshot, IReadOnlyList<AgentSnapshotDto> agents)
    {
        foreach (var source in agents)
        {
            foreach (var probe in source.Heartbeat.Probes)
            {
                var targetHost = ProbeHost(probe);
                if (string.IsNullOrWhiteSpace(targetHost) || IsLoopback(targetHost)) continue;
                var target = agents.FirstOrDefault(a => Aliases(a.Heartbeat).Contains(targetHost, StringComparer.OrdinalIgnoreCase));
                if (target is null || string.Equals(target.Heartbeat.AgentId, source.Heartbeat.AgentId, StringComparison.OrdinalIgnoreCase)) continue;

                var targetNodeId = ResolveTargetDependencyNode(target.Heartbeat, probe);
                snapshot.Edges.Add(Edge(
                    targetNodeId,
                    ProbeId(source.Heartbeat.AgentId, probe.Id),
                    "dependency",
                    $"remote {probe.Type} dependency",
                    true));
            }
        }
    }


    private static string ResolveTargetDependencyNode(AgentHeartbeatRequest target, ProbeMetric remoteProbe)
    {
        if (string.Equals(remoteProbe.Type, "HTTP", StringComparison.OrdinalIgnoreCase) &&
            target.Probes.Any(p => string.Equals(p.Id, "demo-http", StringComparison.OrdinalIgnoreCase)))
            return ProbeId(target.AgentId, "demo-http");

        if (string.Equals(remoteProbe.Type, "TCP", StringComparison.OrdinalIgnoreCase) &&
            target.Probes.Any(p => string.Equals(p.Id, "demo-tcp", StringComparison.OrdinalIgnoreCase)))
            return ProbeId(target.AgentId, "demo-tcp");

        return HostId(target.AgentId);
    }

    private static List<TopologyImpactDto> BuildImpacts(TopologySnapshotDto snapshot)
    {
        var outgoing = snapshot.Edges.Where(e => e.Type == "dependency")
            .GroupBy(e => e.FromNodeId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ToNodeId).Distinct().ToList());
        var nodeById = snapshot.Nodes.ToDictionary(n => n.Id);
        var impacts = new List<TopologyImpactDto>();

        foreach (var root in snapshot.Nodes.Where(n => n.Health == "failed"))
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            queue.Enqueue(root.Id);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!outgoing.TryGetValue(current, out var next)) continue;
                foreach (var id in next)
                {
                    if (visited.Add(id)) queue.Enqueue(id);
                }
            }
            if (visited.Count == 0) continue;
            impacts.Add(new TopologyImpactDto
            {
                RootNodeId = root.Id,
                RootLabel = root.Label,
                AffectedNodeIds = visited.ToList(),
                AffectedLabels = visited.Where(nodeById.ContainsKey).Select(id => nodeById[id].Label).Distinct().ToList()
            });
        }
        return impacts.OrderByDescending(i => i.AffectedNodeIds.Count).Take(20).ToList();
    }

    private static TopologyNodeDto Node(string id, AgentHeartbeatRequest h, string kind, string label, string detail, bool healthy) => new()
    {
        Id = id, AgentId = h.AgentId, Kind = kind, Label = label, Detail = detail,
        Health = healthy ? "healthy" : "failed", Site = h.Site, EnvironmentName = h.EnvironmentName
    };

    private static TopologyEdgeDto Edge(string from, string to, string type, string label, bool crossAgent) => new()
    {
        FromNodeId = from, ToNodeId = to, Type = type, Label = label, CrossAgent = crossAgent
    };

    private static IEnumerable<string> Aliases(AgentHeartbeatRequest h)
    {
        yield return h.AgentId;
        yield return h.MachineName;
        if (!string.IsNullOrWhiteSpace(h.DisplayName)) yield return h.DisplayName;
        foreach (var nic in h.NetworkAdapters)
            if (!string.IsNullOrWhiteSpace(nic.Ipv4Address)) yield return nic.Ipv4Address!;
    }

    private static string? ProbeHost(ProbeMetric probe)
    {
        if (string.Equals(probe.Type, "HTTP", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(probe.Target, UriKind.Absolute, out var uri))
            return uri.Host;
        var target = probe.Target;
        if (target.StartsWith("[", StringComparison.Ordinal))
        {
            var end = target.IndexOf(']');
            return end > 1 ? target[1..end] : target;
        }
        var colon = target.LastIndexOf(':');
        return colon > 0 && target.Count(c => c == ':') == 1 ? target[..colon] : target;
    }

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" || host == "::1";

    private static string HostId(string agentId) => $"agent:{agentId}";
    private static string ProcessId(string agentId, string name) => $"agent:{agentId}:process:{name}";
    private static string ServiceId(string agentId, string name) => $"agent:{agentId}:service:{name}";
    private static string ProbeId(string agentId, string id) => $"agent:{agentId}:probe:{id}";
}
