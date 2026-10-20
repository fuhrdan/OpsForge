using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpsForge.Contracts;

namespace OpsForge.Server;

public sealed class CorrelationRuleCatalog
{
    private readonly object _gate = new();
    private readonly CorrelationOptions _active;
    private readonly string _activePath;
    private readonly string _historyPath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public CorrelationRuleCatalog(IWebHostEnvironment environment, CorrelationOptions active)
    {
        _active = active;
        var root = Environment.GetEnvironmentVariable("OPSFORGE_ROOT");
        var dataDirectory = !string.IsNullOrWhiteSpace(root)
            ? Path.Combine(root, "data")
            : Path.Combine(environment.ContentRootPath, "data");

        Directory.CreateDirectory(dataDirectory);
        _activePath = Path.Combine(dataDirectory, "correlation-rules.active.json");
        _historyPath = Path.Combine(dataDirectory, "correlation-rule-history.jsonl");
        LoadPersistedOverride();
    }

    public IReadOnlyList<CorrelationRule> GetActiveRules()
    {
        lock (_gate) return CloneRules(_active.Rules);
    }

    public CorrelationRuleValidationResultDto Validate(IReadOnlyList<CorrelationRule>? rules)
    {
        var source = rules?.ToList() ?? new List<CorrelationRule>();
        var issues = new List<CorrelationRuleValidationIssueDto>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (source.Count == 0)
            issues.Add(Issue("", "rules", "At least one correlation rule is required."));

        foreach (var rule in source)
        {
            var id = rule.Id?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id)) issues.Add(Issue(id, "id", "Rule ID is required."));
            else
            {
                if (id.Contains(':')) issues.Add(Issue(id, "id", "Rule ID may not contain ':'."));
                if (!ids.Add(id)) issues.Add(Issue(id, "id", "Rule ID must be unique."));
            }

            if (string.IsNullOrWhiteSpace(rule.Title)) issues.Add(Issue(id, "title", "Rule title is required."));
            if (rule.WindowSeconds is < 1 or > 3600) issues.Add(Issue(id, "windowSeconds", "WindowSeconds must be between 1 and 3600."));
            if (rule.MinimumFailures < 2) issues.Add(Issue(id, "minimumFailures", "MinimumFailures must be at least 2."));

            var ruleSelectors = new List<string>();
            if (!string.IsNullOrWhiteSpace(rule.Process)) ruleSelectors.Add($"process:{rule.Process.Trim()}");
            if (!string.IsNullOrWhiteSpace(rule.Service)) ruleSelectors.Add($"windows-service:{rule.Service.Trim()}");
            foreach (var probe in rule.ProbeIds ?? new List<string>())
            {
                var trimmed = probe?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(trimmed)) issues.Add(Issue(id, "probeIds", "Probe IDs may not be empty."));
                else ruleSelectors.Add($"probe:{trimmed}");
            }

            var duplicateWithinRule = ruleSelectors.GroupBy(v => v, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (duplicateWithinRule is not null) issues.Add(Issue(id, "selectors", $"Selector '{duplicateWithinRule.Key}' is repeated in this rule."));
            if (ruleSelectors.Count < rule.MinimumFailures)
                issues.Add(Issue(id, "minimumFailures", $"Rule has {ruleSelectors.Count} selector(s), fewer than MinimumFailures={rule.MinimumFailures}."));

            foreach (var selector in ruleSelectors.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (selectors.TryGetValue(selector, out var otherRule))
                    issues.Add(Issue(id, "selectors", $"Selector '{selector}' overlaps rule '{otherRule}'. Signal selectors may belong to only one rule."));
                else selectors[selector] = id;
            }
        }

        return new CorrelationRuleValidationResultDto
        {
            Valid = issues.Count == 0,
            RuleCount = source.Count,
            Issues = issues
        };
    }

    public CorrelationRulePreviewResultDto Preview(AgentHeartbeatRequest heartbeat, IReadOnlyList<CorrelationRule>? draftRules)
    {
        var rules = draftRules is { Count: > 0 } ? CloneRules(draftRules) : GetActiveRules().ToList();
        var validation = Validate(rules);
        var result = new CorrelationRulePreviewResultDto
        {
            AgentId = heartbeat.AgentId,
            HeartbeatUtc = heartbeat.TimestampUtc,
            Validation = validation
        };
        if (!validation.Valid) return result;

        foreach (var rule in rules)
        {
            var evaluation = CorrelationEngine.Evaluate(heartbeat, rule);
            result.Results.Add(new CorrelationRulePreviewItemDto
            {
                RuleId = rule.Id,
                RuleTitle = rule.Title,
                Complete = evaluation.Complete,
                FailureCount = evaluation.FailureCount,
                WouldOpenPrimaryIncident = evaluation.Candidate is not null,
                Candidate = evaluation.Candidate
            });
        }
        return result;
    }

    public CorrelationRuleHistoryEntryDto Activate(IReadOnlyList<CorrelationRule> rules, string actor, string note)
    {
        var validation = Validate(rules);
        if (!validation.Valid) throw new InvalidOperationException("Correlation rules failed validation.");

        lock (_gate)
        {
            var cloned = CloneRules(rules);
            _active.Rules = cloned;
            PersistActive(cloned);
            var entry = new CorrelationRuleHistoryEntryDto
            {
                VersionId = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..32],
                CreatedUtc = DateTimeOffset.UtcNow,
                Actor = string.IsNullOrWhiteSpace(actor) ? "unknown" : actor.Trim(),
                Note = string.IsNullOrWhiteSpace(note) ? "Correlation rules activated." : note.Trim(),
                Sha256 = ComputeHash(cloned),
                RuleCount = cloned.Count,
                Rules = CloneRules(cloned)
            };
            File.AppendAllText(_historyPath, JsonSerializer.Serialize(entry, _jsonOptions) + Environment.NewLine, Encoding.UTF8);
            return entry;
        }
    }

    public IReadOnlyList<CorrelationRuleHistoryEntryDto> GetHistory(int limit = 100)
    {
        lock (_gate)
        {
            if (!File.Exists(_historyPath)) return Array.Empty<CorrelationRuleHistoryEntryDto>();
            return File.ReadLines(_historyPath)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line =>
                {
                    try { return JsonSerializer.Deserialize<CorrelationRuleHistoryEntryDto>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
                    catch { return null; }
                })
                .Where(entry => entry is not null)
                .Cast<CorrelationRuleHistoryEntryDto>()
                .OrderByDescending(entry => entry.CreatedUtc)
                .Take(Math.Clamp(limit, 1, 500))
                .ToList();
        }
    }

    public CorrelationRuleHistoryEntryDto? Restore(string versionId, string actor)
    {
        var entry = GetHistory(500).FirstOrDefault(item => string.Equals(item.VersionId, versionId, StringComparison.OrdinalIgnoreCase));
        return entry is null ? null : Activate(entry.Rules, actor, $"Restored correlation rule version {entry.VersionId}.");
    }

    public CorrelationRuleExportDto Export()
    {
        var rules = GetActiveRules().ToList();
        return new CorrelationRuleExportDto
        {
            ExportedUtc = DateTimeOffset.UtcNow,
            Sha256 = ComputeHash(rules),
            Rules = rules
        };
    }

    private void LoadPersistedOverride()
    {
        lock (_gate)
        {
            if (!File.Exists(_activePath)) return;
            try
            {
                var export = JsonSerializer.Deserialize<CorrelationRuleExportDto>(File.ReadAllText(_activePath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (export?.Rules is null) return;
                if (Validate(export.Rules).Valid) _active.Rules = CloneRules(export.Rules);
            }
            catch
            {
                // Invalid persisted overrides never prevent startup; packaged rules remain the fallback.
            }
        }
    }

    private void PersistActive(IReadOnlyList<CorrelationRule> rules)
    {
        var export = new CorrelationRuleExportDto
        {
            ExportedUtc = DateTimeOffset.UtcNow,
            Sha256 = ComputeHash(rules),
            Rules = CloneRules(rules)
        };
        var temporary = _activePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(export, _jsonOptions), Encoding.UTF8);
        File.Move(temporary, _activePath, overwrite: true);
    }

    private string ComputeHash(IReadOnlyList<CorrelationRule> rules)
    {
        var canonical = JsonSerializer.Serialize(CloneRules(rules), _jsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static List<CorrelationRule> CloneRules(IEnumerable<CorrelationRule> rules) => rules.Select(rule => new CorrelationRule
    {
        Id = rule.Id?.Trim() ?? string.Empty,
        Title = rule.Title?.Trim() ?? string.Empty,
        Process = string.IsNullOrWhiteSpace(rule.Process) ? null : rule.Process.Trim(),
        Service = string.IsNullOrWhiteSpace(rule.Service) ? null : rule.Service.Trim(),
        ProbeIds = (rule.ProbeIds ?? new List<string>()).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList(),
        WindowSeconds = rule.WindowSeconds,
        MinimumFailures = rule.MinimumFailures
    }).ToList();

    private static CorrelationRuleValidationIssueDto Issue(string ruleId, string field, string message) => new()
    {
        RuleId = ruleId,
        Field = field,
        Severity = "error",
        Message = message
    };
}
