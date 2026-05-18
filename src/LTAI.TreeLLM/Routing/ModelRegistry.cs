using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LTAI.TreeLLM.Routing;

public sealed class ModelRegistry
{
    private readonly ILogger<ModelRegistry> _logger;
    private readonly ConcurrentDictionary<string, ModelProfile> _models = new();

    public ModelRegistry(ILogger<ModelRegistry> logger)
    {
        _logger = logger;
    }

    public void Register(string name, ModelProfile profile)
    {
        _models[name] = profile;
        _logger.LogInformation("Model registered: {Name} (tier:{Tier}, cost:${Cost}/1M)", name, profile.Tier, profile.CostPer1MTokens);
    }

    public ModelProfile? Get(string name) => _models.TryGetValue(name, out var p) ? p : null;

    public IReadOnlyList<ModelProfile> GetAll() => _models.Values.ToList().AsReadOnly();

    public IReadOnlyList<ModelProfile> GetByTier(ModelTier tier) =>
        _models.Values.Where(m => m.Tier == tier).ToList().AsReadOnly();

    public IReadOnlyList<ModelProfile> GetByCapability(string capability) =>
        _models.Values.Where(m => m.Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase)).ToList().AsReadOnly();

    public void UpdateHealth(string name, bool success, double latencyMs)
    {
        if (!_models.TryGetValue(name, out var model)) return;
        var decay = 0.95;
        model.SuccessRate = model.SuccessRate * decay + (success ? 1.0 : 0.0) * (1.0 - decay);
        model.AvgLatencyMs = model.AvgLatencyMs * decay + latencyMs * (1.0 - decay);
        model.TotalCalls++;
        if (success) model.TotalSuccess++;
        model.LastUsed = DateTime.UtcNow;
    }

    public void RecordTokens(string name, int inputTokens, int outputTokens)
    {
        if (_models.TryGetValue(name, out var model))
        {
            model.TotalInputTokens += inputTokens;
            model.TotalOutputTokens += outputTokens;
            model.EstimatedCost += (inputTokens * model.CostPer1MTokens + outputTokens * model.CostPer1MTokens) / 1_000_000.0;
        }
    }

    public IReadOnlyList<ModelProfile> RankByScore(string taskType = "general") =>
        _models.Values.OrderByDescending(m => m.GetScore(taskType)).ToList().AsReadOnly();

    public Dictionary<string, double> GetCostEstimates() =>
        _models.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.EstimatedCost);
}

public sealed class ModelProfile
{
    public string Name { get; init; } = "";
    public ModelTier Tier { get; init; } = ModelTier.Standard;
    public double CostPer1MTokens { get; init; }
    public int MaxTokens { get; init; } = 4096;
    public int MaxContext { get; init; } = 128000;
    public HashSet<string> Capabilities { get; init; } = new();
    public Dictionary<string, double> TaskBonuses { get; init; } = new();

    public double SuccessRate { get; set; } = 1.0;
    public double AvgLatencyMs { get; set; } = 500;
    public long TotalCalls { get; set; }
    public long TotalSuccess { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public double EstimatedCost { get; set; }
    public DateTime LastUsed { get; set; } = DateTime.UtcNow;

    public double GetScore(string taskType)
    {
        var baseScore = SuccessRate * 0.4 + (1.0 / (1.0 + AvgLatencyMs / 1000.0)) * 0.3 + (1.0 / (1.0 + CostPer1MTokens)) * 0.2;
        if (TaskBonuses.TryGetValue(taskType, out var bonus)) baseScore += bonus * 0.1;
        return Math.Min(1.0, baseScore);
    }
}

public enum ModelTier { Flash, Standard, Pro, Ultra }
