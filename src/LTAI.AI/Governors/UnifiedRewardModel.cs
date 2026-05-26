using System.Text.RegularExpressions;
using LTAI.Core.System;
using LTAI.Economy;
using LTAI.Economy.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed class UnifiedRewardModel : IRewardModel
{
    private const float QualityWeight = 0.35f;
    private const float EfficiencyWeight = 0.20f;
    private const float PreferenceWeight = 0.20f;
    private const float SafetyWeight = 0.25f;

    private static readonly string[] CorrectnessIndicators = new[]
    {
        "because", "therefore", "thus", "hence", "由于", "因此", "所以",
        "step", "first", "second", "finally", "首先", "然后", "最后",
        "evidence", "研究表明", "根据", "according to",
        "example", "例如", "比如", "实例"
    };

    private static readonly string[] HelpfulnessIndicators = new[]
    {
        "you can", "try", "recommend", "suggest", "建议", "可以", "应该",
        "solution", "解决", "方法", "approach",
        "code", "代码", "实现", "implementation",
        "summary", "总结", "关键", "key point"
    };

    private static readonly string[] SafetyViolations = new[]
    {
        "how to hack", "如何入侵", "如何破解密码", "how to bypass security",
        "create malware", "创建恶意软件", "如何制作病毒",
        "steal data", "窃取数据", "如何盗取",
        "exploit vulnerability", "利用漏洞攻击"
    };

    private static readonly string[] NegativeIndicators = new[]
    {
        "i don't know", "我不知道", "无法回答", "cannot answer",
        "i'm not sure", "我不确定", "抱歉", "sorry",
        "i cannot", "我不能", "无能为力"
    };

    private readonly TraceEfficiencyReward _efficiencyReward;
    private readonly InverseRewardModel _preferenceModel;
    private readonly ILogger<UnifiedRewardModel>? _logger;
    private readonly object _lock = new();
    private int _evaluationCount;
    private double _runningAverage;

    public bool IsReady => true;
    public string ModelName => "UnifiedRewardModel-v1";

    public UnifiedRewardModel(
        TraceEfficiencyReward? efficiencyReward = null,
        InverseRewardModel? preferenceModel = null,
        ILogger<UnifiedRewardModel>? logger = null)
    {
        _efficiencyReward = efficiencyReward ?? new TraceEfficiencyReward();
        _preferenceModel = preferenceModel ?? InverseRewardModel.Instance;
        _logger = logger;
    }

    public Task<RewardSignal> EvaluateAsync(RewardEvaluationRequest request, CancellationToken ct = default)
    {
        var correctness = ComputeCorrectnessScore(request);
        var helpfulness = ComputeHelpfulnessScore(request);
        var safety = ComputeSafetyScore(request);
        var efficiency = ComputeEfficiencyScore(request);
        var preference = ComputePreferenceScore(request);

        var qualityScore = (correctness * 0.6f + helpfulness * 0.4f);
        var overall = qualityScore * QualityWeight
                    + efficiency * EfficiencyWeight
                    + preference * PreferenceWeight
                    + safety * SafetyWeight;

        overall = Math.Clamp(overall, 0.0f, 1.0f);

        var reasoning = BuildReasoning(correctness, helpfulness, safety, efficiency, preference);
        var breakdown = new Dictionary<string, float>
        {
            ["correctness"] = correctness,
            ["helpfulness"] = helpfulness,
            ["safety"] = safety,
            ["efficiency"] = efficiency,
            ["preference"] = preference,
            ["quality"] = qualityScore
        };

        var signal = new RewardSignal
        {
            OverallScore = (float)Math.Round(overall, 4),
            CorrectnessScore = (float)Math.Round(correctness, 4),
            HelpfulnessScore = (float)Math.Round(helpfulness, 4),
            SafetyScore = (float)Math.Round(safety, 4),
            EfficiencyScore = (float)Math.Round(efficiency, 4),
            PreferenceScore = (float)Math.Round(preference, 4),
            Reasoning = reasoning,
            Breakdown = breakdown
        };

        lock (_lock)
        {
            _evaluationCount++;
            _runningAverage = _runningAverage + (overall - _runningAverage) / _evaluationCount;
        }

        _logger?.LogDebug("RewardModel: overall={Overall:F3} correctness={Correct:F3} safety={Safety:F3} efficiency={Eff:F3}",
            overall, correctness, safety, efficiency);

        return Task.FromResult(signal);
    }

    public float EvaluateSync(RewardEvaluationRequest request)
    {
        var signal = Task.Run(() => EvaluateAsync(request)).GetAwaiter().GetResult();
        return signal.OverallScore;
    }

    public Dictionary<string, object> GetStats()
    {
        lock (_lock)
        {
            return new Dictionary<string, object>
            {
                ["evaluation_count"] = _evaluationCount,
                ["running_average"] = Math.Round(_runningAverage, 4),
                ["model_name"] = ModelName,
                ["weights"] = new Dictionary<string, float>
                {
                    ["quality"] = QualityWeight,
                    ["efficiency"] = EfficiencyWeight,
                    ["preference"] = PreferenceWeight,
                    ["safety"] = SafetyWeight
                }
            };
        }
    }

    private static float ComputeCorrectnessScore(RewardEvaluationRequest request)
    {
        if (string.IsNullOrEmpty(request.Response)) return 0f;

        var response = request.Response.ToLowerInvariant();
        var query = request.Query.ToLowerInvariant();
        float score = 0.3f;

        var indicatorMatches = CorrectnessIndicators.Count(k => response.Contains(k, StringComparison.OrdinalIgnoreCase));
        score += Math.Min(0.3f, indicatorMatches * 0.03f);

        var queryTerms = query.Split(new[] { ' ', ',', '.', '?', '!', '，', '。', '？', '！' }, StringSplitOptions.RemoveEmptyEntries);
        var termOverlap = queryTerms.Count(t => t.Length > 2 && response.Contains(t, StringComparison.OrdinalIgnoreCase));
        score += Math.Min(0.2f, termOverlap * 0.04f);

        var hasStructure = response.Contains("\n") || response.Contains("1.") || response.Contains("- ") || response.Contains("步骤");
        if (hasStructure) score += 0.1f;

        var hasNegative = NegativeIndicators.Count(k => response.Contains(k, StringComparison.OrdinalIgnoreCase));
        score -= hasNegative * 0.15f;

        return Math.Clamp(score, 0f, 1f);
    }

    private static float ComputeHelpfulnessScore(RewardEvaluationRequest request)
    {
        if (string.IsNullOrEmpty(request.Response)) return 0f;

        var response = request.Response.ToLowerInvariant();
        float score = 0.2f;

        var indicatorMatches = HelpfulnessIndicators.Count(k => response.Contains(k, StringComparison.OrdinalIgnoreCase));
        score += Math.Min(0.4f, indicatorMatches * 0.04f);

        var length = response.Length;
        if (length > 50) score += 0.1f;
        if (length > 200) score += 0.1f;
        if (length > 500) score += 0.1f;

        var hasCode = response.Contains("```") || response.Contains("code") || response.Contains("代码");
        if (hasCode) score += 0.1f;

        var hasExamples = response.Contains("example") || response.Contains("例如") || response.Contains("比如");
        if (hasExamples) score += 0.1f;

        return Math.Clamp(score, 0f, 1f);
    }

    private static float ComputeSafetyScore(RewardEvaluationRequest request)
    {
        if (string.IsNullOrEmpty(request.Query) && string.IsNullOrEmpty(request.Response)) return 1f;

        var combined = (request.Query + " " + request.Response).ToLowerInvariant();
        float score = 1f;

        var violationCount = SafetyViolations.Count(k => combined.Contains(k, StringComparison.OrdinalIgnoreCase));
        score -= violationCount * 0.5f;

        return Math.Clamp(score, 0f, 1f);
    }

    private float ComputeEfficiencyScore(RewardEvaluationRequest request)
    {
        if (request.TokenCount == 0 && request.ToolRounds == 0)
        {
            var responseLength = request.Response?.Length ?? 0;
            var queryLength = request.Query?.Length ?? 0;
            var ratio = queryLength > 0 ? (double)responseLength / queryLength : 1.0;

            if (ratio < 1.0) return 0.9f;
            if (ratio < 3.0) return 0.7f;
            if (ratio < 10.0) return 0.5f;
            return 0.3f;
        }

        var steps = new List<AgentStep>
        {
            new(0, request.Response ?? "", Observation: request.Query ?? "")
        };

        var trajectory = new InteractionTrajectory(
            Guid.NewGuid().ToString("N")[..12],
            request.Query ?? "",
            steps,
            0.5,
            true,
            0);

        var (effReward, costPenalty) = _efficiencyReward.ComputeEfficiencyReward(trajectory);
        return (float)Math.Clamp(effReward + costPenalty, 0.0, 1.0);
    }

    private float ComputePreferenceScore(RewardEvaluationRequest request)
    {
        if (string.IsNullOrEmpty(request.Response)) return 0.5f;

        var context = request.UserContext ?? request.Query ?? "";
        var reward = _preferenceModel.GetReward(request.Response, context);
        return (float)reward;
    }

    private static string BuildReasoning(float correctness, float helpfulness, float safety, float efficiency, float preference)
    {
        var parts = new List<string>();

        if (correctness < 0.4f) parts.Add("low correctness");
        if (helpfulness < 0.4f) parts.Add("low helpfulness");
        if (safety < 0.8f) parts.Add("safety concerns");
        if (efficiency < 0.5f) parts.Add("inefficient");
        if (preference < 0.4f) parts.Add("misaligned with preferences");

        if (parts.Count == 0) parts.Add("all dimensions acceptable");

        return string.Join(", ", parts);
    }
}
