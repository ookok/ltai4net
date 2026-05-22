using System.Collections.Concurrent;
using LTAI.Agent.Intelligence;
using LTAI.Agent.Models;
using LTAI.Agent.Prompting;

namespace LTAI.Agent.Routing;

public sealed record MetricSnapshot
{
    public double LatencyMs { get; init; }
    public double Accuracy { get; init; }
    public double Cost { get; init; }
    public string TaskType { get; init; } = "general";
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public sealed record TunedParameters
{
    public int RAGTopK { get; set; } = 5;
    public double Temperature { get; set; } = 0.3;
    public int MaxTokens { get; set; } = 4096;
    public int CacheTTLSeconds { get; set; } = 300;
    public Dictionary<string, double> ProviderWeights { get; set; } = new();
}

public sealed class AutoTunerBridge
{
    private readonly ThompsonStrategy _thompson;
    private readonly AutoPrompt _autoPrompt;
    private readonly ConcurrentDictionary<string, BetaBelief> _parameterBeliefs = new();
    private readonly ConcurrentDictionary<string, TunedParameters> _taskParameters = new();
    private readonly Random _rng = new();

    private static readonly string[] TunableParams =
        ["rag_topk", "temperature", "max_tokens", "cache_ttl"];

    public AutoTunerBridge(ThompsonStrategy thompson, AutoPrompt? autoPrompt = null)
    {
        _thompson = thompson;
        _autoPrompt = autoPrompt ?? AutoPrompt.Instance;
        InitializeBeliefs();
    }

    private void InitializeBeliefs()
    {
        foreach (var param in TunableParams)
        {
            _parameterBeliefs.TryAdd(param, new BetaBelief(2.0, 1.0));
        }
    }

    public TunedParameters LearnOutcome(MetricSnapshot snapshot)
    {
        UpdateBeliefs(snapshot);

        var taskType = snapshot.TaskType;

        var ragTopK = GetParameterInt("rag_topk", 3, 20, taskType, 5);
        var temperature = GetParameterDouble("temperature", 0.1, 1.0, taskType, 0.3);
        var maxTokens = GetParameterInt("max_tokens", 1024, 32768, taskType, 4096);
        var cacheTTL = GetParameterInt("cache_ttl", 60, 1800, taskType, 300);

        var existing = _taskParameters.GetOrAdd(taskType, _ => new TunedParameters());
        existing.RAGTopK = ragTopK;
        existing.Temperature = temperature;
        existing.MaxTokens = maxTokens;
        existing.CacheTTLSeconds = cacheTTL;
        existing.ProviderWeights = GetProviderWeights(taskType);

        return existing;
    }

    public Dictionary<string, object?> ExportParams(string taskType = "general")
    {
        var tuned = _taskParameters.GetOrAdd(taskType, _ => new TunedParameters());

        return new Dictionary<string, object?>
        {
            ["rag_topk"] = tuned.RAGTopK,
            ["temperature"] = tuned.Temperature,
            ["max_tokens"] = tuned.MaxTokens,
            ["cache_ttl_seconds"] = tuned.CacheTTLSeconds,
            ["provider_weights"] = tuned.ProviderWeights,
            ["task_type"] = taskType
        };
    }

    public void SetProviderWeight(string taskType, string provider, double weight)
    {
        var tuned = _taskParameters.GetOrAdd(taskType, _ => new TunedParameters());
        tuned.ProviderWeights[provider] = Math.Clamp(weight, 0.0, 1.0);
    }

    public Dictionary<string, object> GetStats()
    {
        var stats = new Dictionary<string, object>
        {
            ["task_types"] = _taskParameters.Count,
            ["parameters_tracked"] = _parameterBeliefs.Count
        };

        foreach (var (param, belief) in _parameterBeliefs)
        {
            stats[$"belief_{param}_alpha"] = belief.Alpha;
            stats[$"belief_{param}_beta"] = belief.Beta;
            stats[$"belief_{param}_mean"] = belief.Mean;
        }

        return stats;
    }

    private void UpdateBeliefs(MetricSnapshot snapshot)
    {
        var success = snapshot.Accuracy > 0.5 && snapshot.LatencyMs < 10000;

        _parameterBeliefs.AddOrUpdate("rag_topk",
            _ => new BetaBelief(2.0, 1.0),
            (_, b) => { if (success) b.Observe(snapshot.Accuracy > 0.7); else b.Observe(false); return b; });

        _parameterBeliefs.AddOrUpdate("temperature",
            _ => new BetaBelief(2.0, 1.0),
            (_, b) => { b.Observe(snapshot.LatencyMs < 5000); return b; });

        _parameterBeliefs.AddOrUpdate("max_tokens",
            _ => new BetaBelief(2.0, 1.0),
            (_, b) => { b.Observe(snapshot.Cost < 0.01); return b; });

        _parameterBeliefs.AddOrUpdate("cache_ttl",
            _ => new BetaBelief(2.0, 1.0),
            (_, b) => { b.Observe(success); return b; });
    }

    private int GetParameterInt(string paramName, int min, int max, string taskType, int fallback)
    {
        var belief = _parameterBeliefs.GetOrAdd(paramName, _ => new BetaBelief(2.0, 1.0));
        var sample = belief.Sample(_rng);
        var value = (int)(min + sample * (max - min));
        return Math.Clamp(value, min, max);
    }

    private double GetParameterDouble(string paramName, double min, double max, string taskType, double fallback)
    {
        var belief = _parameterBeliefs.GetOrAdd(paramName, _ => new BetaBelief(2.0, 1.0));
        var sample = belief.Sample(_rng);
        var value = min + sample * (max - min);
        return Math.Round(Math.Clamp(value, min, max), 2);
    }

    private Dictionary<string, double> GetProviderWeights(string taskType)
    {
        var stats = _thompson.Stats();
        var weights = new Dictionary<string, double>();

        foreach (var (key, value) in stats)
        {
            if (key.StartsWith("arm_") && key.EndsWith("_quality"))
            {
                var provider = key["arm_".Length..^"_quality".Length];
                weights[provider] = value is double d ? Math.Round(Math.Clamp(d, 0.0, 1.0), 3) : 0.5;
            }
        }

        return weights;
    }

    public TunedParameters SuggestParameters(string taskType = "general")
    {
        return _taskParameters.GetOrAdd(taskType, _ =>
        {
            var p = new TunedParameters();
            p.RAGTopK = GetParameterInt("rag_topk", 3, 20, taskType, 5);
            p.Temperature = GetParameterDouble("temperature", 0.1, 1.0, taskType, 0.3);
            p.MaxTokens = GetParameterInt("max_tokens", 1024, 32768, taskType, 4096);
            p.CacheTTLSeconds = GetParameterInt("cache_ttl", 60, 1800, taskType, 300);
            p.ProviderWeights = GetProviderWeights(taskType);
            return p;
        });
    }
}
