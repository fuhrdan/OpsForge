using System.Text.Json;

namespace OpsForge.Server;

public sealed class SloConfiguration
{
    public bool Enabled { get; set; } = true;
    public int EvaluationIntervalSeconds { get; set; } = 60;
    public List<SloDefinition> Definitions { get; set; } = new();

    public static SloConfiguration Load(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, "slo-rules.json");
        if (!File.Exists(path)) return new SloConfiguration();
        var root = JsonSerializer.Deserialize<SloConfigurationRoot>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var configuration = root?.Slo ?? new SloConfiguration();
        configuration.Validate();
        return configuration;
    }

    public void Validate()
    {
        EvaluationIntervalSeconds = Math.Clamp(EvaluationIntervalSeconds, 30, 3600);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in Definitions)
        {
            definition.Id = definition.Id.Trim();
            definition.Name = definition.Name.Trim();
            if (string.IsNullOrWhiteSpace(definition.Id) || !ids.Add(definition.Id)) throw new InvalidOperationException("SLO definitions require unique non-empty IDs.");
            if (string.IsNullOrWhiteSpace(definition.Name)) definition.Name = definition.Id;
            definition.ObjectivePercent = Math.Clamp(definition.ObjectivePercent, 90.0, 99.999);
            definition.ComplianceWindowHours = Math.Clamp(definition.ComplianceWindowHours, 1, 24 * 30);
            definition.AgentIds = definition.AgentIds.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var policyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var policy in definition.BurnRatePolicies)
            {
                policy.Id = policy.Id.Trim();
                if (string.IsNullOrWhiteSpace(policy.Id) || !policyIds.Add(policy.Id)) throw new InvalidOperationException($"SLO {definition.Id} requires unique non-empty burn-rate policy IDs.");
                policy.ShortWindowMinutes = Math.Clamp(policy.ShortWindowMinutes, 1, 1440);
                policy.LongWindowMinutes = Math.Clamp(policy.LongWindowMinutes, policy.ShortWindowMinutes, 10080);
                policy.ShortBurnRateThreshold = Math.Clamp(policy.ShortBurnRateThreshold, 0.1, 10000.0);
                policy.LongBurnRateThreshold = Math.Clamp(policy.LongBurnRateThreshold, 0.1, 10000.0);
                policy.Severity = string.IsNullOrWhiteSpace(policy.Severity) ? "warning" : policy.Severity.Trim().ToLowerInvariant();
                if (policy.Severity is not ("critical" or "warning" or "info")) throw new InvalidOperationException($"SLO {definition.Id} policy {policy.Id} has unsupported severity {policy.Severity}.");
            }
        }
    }

    public SloStatusDto GetStatus() => new()
    {
        Enabled = Enabled,
        EvaluationIntervalSeconds = EvaluationIntervalSeconds,
        DefinitionCount = Definitions.Count,
        EnabledDefinitionCount = Definitions.Count(d => d.Enabled),
        Definitions = Definitions.Select(d => new SloDefinitionStatusDto
        {
            Id = d.Id, Name = d.Name, Enabled = d.Enabled, ObjectivePercent = d.ObjectivePercent,
            ComplianceWindowHours = d.ComplianceWindowHours, AgentIds = d.AgentIds,
            BurnRatePolicyCount = d.BurnRatePolicies.Count
        }).ToList()
    };

    public sealed class SloConfigurationRoot { public SloConfiguration Slo { get; set; } = new(); }
}
