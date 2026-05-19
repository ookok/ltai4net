using System.Text.Json;

namespace LTAI.DNA.Meta;

public sealed class MetaStrategy
{
    public ObservationStrategy Observation { get; set; } = new();
    public GenerationStrategy Generation { get; set; } = new();
    public DeploymentStrategy Deployment { get; set; } = new();

    private readonly List<MetaStrategyVersion> _versions = new();
    private int _versionCounter;
    private readonly object _lock = new();

    public MetaStrategyVersion Snapshot(string reason)
    {
        lock (_lock)
        {
            _versionCounter++;
            var version = new MetaStrategyVersion
            {
                Version = _versionCounter,
                Observation = Clone(Observation),
                Generation = Clone(Generation),
                Deployment = Clone(Deployment),
                Reason = reason,
                CreatedAt = DateTime.UtcNow
            };
            _versions.Add(version);
            if (_versions.Count > 100) _versions.RemoveAt(0);
            return version;
        }
    }

    public Dictionary<string, object> ToDict()
    {
        return new Dictionary<string, object>
        {
            ["observation"] = new Dictionary<string, object>
            {
                ["hub_analysis"] = new Dictionary<string, object>
                {
                    ["max_uncovered"] = Observation.HubAnalysisMaxUncovered,
                    ["max_errors"] = Observation.HubAnalysisMaxErrors,
                    ["max_patterns"] = Observation.HubAnalysisMaxPatterns
                },
                ["custom_patterns"] = Observation.CustomPatterns
            },
            ["generation"] = new Dictionary<string, object>
            {
                ["temperature"] = Generation.Temperature,
                ["max_tokens"] = Generation.MaxTokens,
                ["cot_steps"] = Generation.CotSteps,
                ["mutation_rate"] = Generation.MutationRate,
                ["crossover_rate"] = Generation.CrossoverRate,
                ["population_size"] = Generation.PopulationSize,
                ["max_generations"] = Generation.MaxGenerations,
                ["prompt_prefix"] = Generation.PromptPrefix
            },
            ["deployment"] = new Dictionary<string, object>
            {
                ["quality_threshold"] = Deployment.QualityThreshold,
                ["require_hitl_approval"] = Deployment.RequireHitlApproval,
                ["auto_deploy_min_score"] = Deployment.AutoDeployMinScore,
                ["max_auto_deploy_per_session"] = Deployment.MaxAutoDeployPerSession,
                ["rollback_on_test_failure"] = Deployment.RollbackOnTestFailure,
                ["max_consecutive_rollbacks"] = Deployment.MaxConsecutiveRollbacks,
                ["side_git_enabled"] = Deployment.SideGitEnabled
            }
        };
    }

    public static MetaStrategy FromDict(Dictionary<string, object> dict)
    {
        var ms = new MetaStrategy();

        if (dict.TryGetValue("observation", out var obs) && obs is Dictionary<string, object> obsDict)
        {
            if (obsDict.TryGetValue("hub_analysis", out var ha) && ha is Dictionary<string, object> hab)
            {
                ms.Observation.HubAnalysisMaxUncovered =
                    Convert.ToInt32(hab.GetValueOrDefault("max_uncovered", 10));
                ms.Observation.HubAnalysisMaxErrors =
                    Convert.ToInt32(hab.GetValueOrDefault("max_errors", 20));
                ms.Observation.HubAnalysisMaxPatterns =
                    Convert.ToInt32(hab.GetValueOrDefault("max_patterns", 15));
            }

            if (obsDict.TryGetValue("custom_patterns", out var cp) && cp is List<object> cpl)
                ms.Observation.CustomPatterns = cpl.Select(c => c.ToString() ?? "").ToList();
        }

        if (dict.TryGetValue("generation", out var gen) && gen is Dictionary<string, object> genDict)
        {
            ms.Generation.Temperature = Convert.ToDouble(genDict.GetValueOrDefault("temperature", 0.8));
            ms.Generation.MaxTokens = Convert.ToInt32(genDict.GetValueOrDefault("max_tokens", 8192));
            ms.Generation.CotSteps = Convert.ToInt32(genDict.GetValueOrDefault("cot_steps", 2));
            ms.Generation.MutationRate =
                Convert.ToDouble(genDict.GetValueOrDefault("mutation_rate", 0.3));
            ms.Generation.CrossoverRate =
                Convert.ToDouble(genDict.GetValueOrDefault("crossover_rate", 0.5));
            ms.Generation.PopulationSize =
                Convert.ToInt32(genDict.GetValueOrDefault("population_size", 32));
            ms.Generation.MaxGenerations =
                Convert.ToInt32(genDict.GetValueOrDefault("max_generations", 24));
            ms.Generation.PromptPrefix =
                genDict.GetValueOrDefault("prompt_prefix", "") as string ?? "";
        }

        if (dict.TryGetValue("deployment", out var dep) && dep is Dictionary<string, object> depDict)
        {
            ms.Deployment.QualityThreshold =
                Convert.ToDouble(depDict.GetValueOrDefault("quality_threshold", 0.6));
            ms.Deployment.RequireHitlApproval =
                Convert.ToBoolean(depDict.GetValueOrDefault("require_hitl_approval", true));
            ms.Deployment.AutoDeployMinScore =
                Convert.ToDouble(depDict.GetValueOrDefault("auto_deploy_min_score", 0.9));
            ms.Deployment.MaxAutoDeployPerSession =
                Convert.ToInt32(depDict.GetValueOrDefault("max_auto_deploy_per_session", 3));
            ms.Deployment.RollbackOnTestFailure =
                Convert.ToBoolean(depDict.GetValueOrDefault("rollback_on_test_failure", true));
            ms.Deployment.MaxConsecutiveRollbacks =
                Convert.ToInt32(depDict.GetValueOrDefault("max_consecutive_rollbacks", 3));
            ms.Deployment.SideGitEnabled =
                Convert.ToBoolean(depDict.GetValueOrDefault("side_git_enabled", true));
        }

        return ms;
    }

    public bool Apply(Dictionary<string, object> edits)
    {
        try
        {
            Snapshot("auto_edit");
            var updated = FromDict(edits);
            Observation = updated.Observation;
            Generation = updated.Generation;
            Deployment = updated.Deployment;
            return true;
        }
        catch { return false; }
    }

    public bool RollbackTo(int version)
    {
        lock (_lock)
        {
            var target = _versions.Find(v => v.Version == version);
            if (target == null) return false;
            Observation = target.Observation;
            Generation = target.Generation;
            Deployment = target.Deployment;
            return true;
        }
    }

    public string DescribeChanges()
    {
        var lines = new List<string>();
        lines.Add($"Observation: {Observation.HubAnalysisMaxUncovered} uncovered, {Observation.HubAnalysisMaxErrors} errors");
        lines.Add($"Generation: T={Generation.Temperature:F2}, CoT={Generation.CotSteps}, mut={Generation.MutationRate:F2}");
        lines.Add($"Deployment: Q={Deployment.QualityThreshold:F2}, HITL={Deployment.RequireHitlApproval}, auto={Deployment.AutoDeployMinScore:F2}");
        return string.Join("\n", lines);
    }

    private static T Clone<T>(T source) where T : class, new()
    {
        var json = JsonSerializer.Serialize(source);
        return JsonSerializer.Deserialize<T>(json) ?? new T();
    }
}

public sealed class ObservationStrategy
{
    public int HubAnalysisMaxUncovered { get; set; } = 10;
    public int HubAnalysisMaxErrors { get; set; } = 20;
    public int HubAnalysisMaxPatterns { get; set; } = 15;
    public List<string> CustomPatterns { get; set; } = new();
}

public sealed class GenerationStrategy
{
    public double Temperature { get; set; } = 0.8;
    public int MaxTokens { get; set; } = 8192;
    public int CotSteps { get; set; } = 2;
    public double MutationRate { get; set; } = 0.3;
    public double CrossoverRate { get; set; } = 0.5;
    public int PopulationSize { get; set; } = 32;
    public int MaxGenerations { get; set; } = 24;
    public string PromptPrefix { get; set; } = "";
}

public sealed class DeploymentStrategy
{
    public double QualityThreshold { get; set; } = 0.6;
    public bool RequireHitlApproval { get; set; } = true;
    public double AutoDeployMinScore { get; set; } = 0.9;
    public int MaxAutoDeployPerSession { get; set; } = 3;
    public bool RollbackOnTestFailure { get; set; } = true;
    public int MaxConsecutiveRollbacks { get; set; } = 3;
    public bool SideGitEnabled { get; set; } = true;
}

public sealed class MetaStrategyVersion
{
    public int Version { get; init; }
    public ObservationStrategy Observation { get; init; } = new();
    public GenerationStrategy Generation { get; init; } = new();
    public DeploymentStrategy Deployment { get; init; } = new();
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public string Reason { get; init; } = "";
}

public sealed class MetaStrategyEngine
{
    private MetaStrategy _strategy = new();
    private MetaMemory? _memory;
    private double _explorationRate = 0.15;
    private int _explorationCount;
    private int _exploitationCount;
    private readonly object _lock = new();

    public MetaStrategy Strategy => _strategy;

    public void BindMemory(MetaMemory memory)
    {
        _memory = memory;
    }

    public async Task<Dictionary<string, object>> ReviewAndEvolve(string domain,
        Func<string, Task<string>> chat)
    {
        if (_memory == null)
            return new() { ["error"] = "MetaMemory not bound" };

        var stats = _memory.Stats();
        var gating = _memory.GatingCalibration();
        var underperforming = _memory.UnderperformingStrategies(domain);
        var decaying = _memory.StrategyDecayTracker("mutation");

        var prompt = $"DGM-H Meta-Agent Review for domain '{domain}':\n" +
                     $"Gating: precision={gating.precision:F2} recall={gating.recall:F2}\n" +
                     $"Underperforming: {string.Join(", ", underperforming)}\n" +
                     $"Decay: early={decaying["early_success_rate"]} late={decaying["late_success_rate"]}\n" +
                     $"Current strategy: {_strategy.DescribeChanges()}\n\n" +
                     "Propose strategy edits as JSON. Available keys: observation, generation, deployment.";

        var response = await chat(prompt);

        try
        {
            var edits = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(response);
            if (edits != null && edits.Count > 0)
            {
                _strategy.Apply(edits);
                _explorationRate = Math.Max(0.05, _explorationRate * 0.98);
            }
        }
        catch { }

        lock (_lock)
        {
            _exploitationCount++;
            if (ShouldExplore())
            {
                _explorationCount++;
                _memory?.RecordGating("explore", true, true, domain);
            }
        }

        return new Dictionary<string, object>
        {
            ["exploration_rate"] = _explorationRate,
            ["exploration_count"] = _explorationCount,
            ["exploitation_count"] = _exploitationCount,
            ["strategy"] = _strategy.DescribeChanges()
        };
    }

    public bool ShouldExplore()
    {
        return Random.Shared.NextDouble() < _explorationRate;
    }

    public string? ForceColdExploration(string strategyType, string domain)
    {
        if (_memory == null) return null;
        var underperforming = _memory.UnderperformingStrategies(domain, 0.3);
        if (underperforming.Count > 0)
            return underperforming[Random.Shared.Next(underperforming.Count)];
        return null;
    }

    public string? TryExplore(string strategyType, string domain)
    {
        if (!ShouldExplore()) return null;
        var result = ForceColdExploration(strategyType, domain);
        if (result != null)
        {
            _memory?.RecordGating(result, true, false, domain);
        }
        return result;
    }

    public void RecordExplorationOutcome()
    {
        _memory?.RecordGating("explore", true, true, "default");
    }

    public void RecordExploitationOutcome()
    {
        _memory?.RecordGating("exploit", false, true, "default");
    }

    public double ExplorationRatio =>
        _explorationCount + _exploitationCount > 0
            ? (double)_explorationCount / (_explorationCount + _exploitationCount)
            : 0;

    public bool Rollback(int version) => _strategy.RollbackTo(version);

    public Dictionary<string, object> GetStatus()
    {
        return new Dictionary<string, object>
        {
            ["exploration_rate"] = _explorationRate,
            ["exploration_ratio"] = ExplorationRatio,
            ["strategy"] = _strategy.DescribeChanges(),
            ["versions"] = _explorationCount + _exploitationCount
        };
    }
}
