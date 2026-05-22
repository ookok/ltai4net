using LTAI.Economy;
using LTAI.Economy.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed record RewardSignal
{
    public float OverallScore { get; init; }
    public float CorrectnessScore { get; init; }
    public float HelpfulnessScore { get; init; }
    public float SafetyScore { get; init; }
    public float EfficiencyScore { get; init; }
    public float PreferenceScore { get; init; }
    public string Reasoning { get; init; } = "";
    public Dictionary<string, float> Breakdown { get; init; } = new();
}

public sealed record RewardEvaluationRequest
{
    public string Query { get; init; } = "";
    public string Response { get; init; } = "";
    public string? Route { get; init; }
    public float Complexity { get; init; }
    public LearningStatus? LearningStatus { get; init; }
    public double? DeltaNorm { get; init; }
    public int TokenCount { get; init; }
    public int ToolRounds { get; init; }
    public string? UserContext { get; init; }
}

public interface IRewardModel
{
    Task<RewardSignal> EvaluateAsync(RewardEvaluationRequest request, CancellationToken ct = default);
    bool IsReady { get; }
    string ModelName { get; }
}
