using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LTAI.AI.Governors;

public enum InterventionType { EnvironmentContract, ProceduralSkill, ActionRealization, TrajectoryRegulation }

public sealed record HarnessIntervention
{
    public string Id { get; init; } = $"hi_{Guid.NewGuid():N}"[..16];
    public InterventionType Type { get; init; }
    public string TriggerPattern { get; init; } = "";
    public string Action { get; init; } = "";
    public float Confidence { get; init; } = 0.5f;
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastApplied { get; set; } = DateTime.UtcNow;
    public float Effectiveness => (SuccessCount + FailureCount) > 0 ? (float)SuccessCount / (SuccessCount + FailureCount) : 0;
    public bool IsActive => Effectiveness >= 0.6f || (SuccessCount + FailureCount) < 3;
}

public sealed class HarnessEvolution
{
    private readonly ConcurrentDictionary<string, HarnessIntervention> _interventions = new();
    private readonly ICrossRunEvolutionStore? _evolutionStore;
    private readonly string _persistPath;
    private readonly Lock _saveLock = new();

    public IReadOnlyDictionary<string, HarnessIntervention> Interventions => _interventions;
    public int Count => _interventions.Count;

    public HarnessEvolution(ICrossRunEvolutionStore? evolutionStore = null, string? persistPath = null)
    {
        _evolutionStore = evolutionStore;
        _persistPath = persistPath ?? Path.Combine(AppContext.BaseDirectory, ".livingtree", "harness_evolution.json");
        Load();
    }

    public HarnessIntervention? Learn(string failurePattern, string failureContext, InterventionType type, string suggestedAction)
    {
        var existing = _interventions.Values.FirstOrDefault(i => i.TriggerPattern == failurePattern);
        if (existing != null)
        {
            existing.FailureCount++;
            if (existing.Effectiveness < 0.3f)
            {
                _interventions.TryUpdate(existing.Id,
                    existing with { Action = suggestedAction, Confidence = Math.Max(0.1f, existing.Confidence - 0.1f) },
                    existing);
            }
            return existing;
        }

        var intervention = new HarnessIntervention
        {
            Type = type,
            TriggerPattern = failurePattern,
            Action = suggestedAction,
            Confidence = 0.5f,
            SuccessCount = 0,
            FailureCount = 1
        };

        _interventions.TryAdd(intervention.Id, intervention);

        _evolutionStore?.RecordLesson(new EvolutionLesson
        {
            Category = $"Harness{type}",
            Severity = 0.5f,
            Summary = $"New {type} intervention: {failurePattern[..Math.Min(failurePattern.Length, 60)]}",
            Mitigation = suggestedAction[..Math.Min(suggestedAction.Length, 60)],
            SourceStage = "harness_evolution"
        });

        Save();
        return intervention;
    }

    /// Apply harness interventions (advisory prefix) to a query before execution.
    public string ApplyHarnessToQuery(string query)
    {
        var applied = new List<HarnessIntervention>();

        foreach (var intervention in _interventions.Values.Where(i => i.IsActive))
        {
            try
            {
                if (Regex.IsMatch(query, intervention.TriggerPattern, RegexOptions.IgnoreCase))
                {
                    applied.Add(intervention);
                    intervention.LastApplied = DateTime.UtcNow;
                    intervention.SuccessCount++;
                }
            }
            catch { }
        }

        if (applied.Count > 0)
        {
            var interventionsText = string.Join("\n", applied.Select(i =>
                $"- [{i.Type}] {i.Action}"));
            return $"[Harness Interventions Active ({applied.Count})]\n{interventionsText}\n\n{query}";
        }

        return query;
    }

    public bool ValidateEnvironmentContract(string toolName, string args, out string? contractViolation)
    {
        contractViolation = null;

        var contracts = _interventions.Values
            .Where(i => i.Type == InterventionType.EnvironmentContract && i.IsActive)
            .ToList();

        foreach (var contract in contracts)
        {
            if (args.Contains(contract.TriggerPattern, StringComparison.OrdinalIgnoreCase))
            {
                contractViolation = contract.Action;
                contract.FailureCount++;
                return false;
            }
        }

        return true;
    }

    public void RecordResult(string interventionId, bool wasHelpful)
    {
        if (_interventions.TryGetValue(interventionId, out var intervention))
        {
            if (wasHelpful)
                intervention.SuccessCount++;
            else
                intervention.FailureCount++;

            if (!intervention.IsActive)
            {
                _evolutionStore?.RecordLesson(new EvolutionLesson
                {
                    Category = "HarnessDeactivation",
                    Severity = 0.4f,
                    Summary = $"Deactivated: {intervention.TriggerPattern}",
                    Mitigation = $"Effectiveness dropped to {intervention.Effectiveness:F2}",
                    SourceStage = "harness_evolution"
                });
            }
        }
    }

    public List<string> ExtractProceduralSkill(List<string> toolSequence, string domain)
    {
        var sequence = string.Join("→", toolSequence);
        var existing = _interventions.Values.FirstOrDefault(i =>
            i.Type == InterventionType.ProceduralSkill && i.TriggerPattern.Contains(domain));

        if (existing == null && toolSequence.Count >= 2)
        {
            Learn($"skill_{domain}_{sequence.GetHashCode():X}", sequence,
                InterventionType.ProceduralSkill,
                $"Procedure: execute {sequence} for {domain} tasks. Cache intermediate results.");
        }

        return toolSequence;
    }

    public string? RegulateTrajectory(int toolCallCount, double elapsedMs, string currentTool)
    {
        if (toolCallCount > 20)
            return $"Loop detected after {toolCallCount} tool calls. Consider: break current sequence, return partial results.";

        if (elapsedMs > 120000 && toolCallCount > 5)
            return $"Slow trajectory ({elapsedMs / 1000:F0}s, {toolCallCount} tools). Consider: parallel execution, caching.";

        return null;
    }

    public void Save()
    {
        lock (_saveLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_persistPath);
                if (dir != null) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(_interventions.Values.ToList());
                File.WriteAllText(_persistPath, json);
            }
            catch { }
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_persistPath))
            {
                var json = File.ReadAllText(_persistPath);
                var interventions = JsonSerializer.Deserialize<List<HarnessIntervention>>(json);
                if (interventions != null)
                    foreach (var i in interventions)
                        _interventions.TryAdd(i.Id, i);
            }
        }
        catch { }
    }
}
