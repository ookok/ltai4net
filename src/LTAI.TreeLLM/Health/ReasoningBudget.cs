using System.Collections.Concurrent;
using LTAI.TreeLLM.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.TreeLLM.Routing;

public sealed class ReasoningBudgetEngine
{
    private static readonly Dictionary<ReasoningTier, int> TIER_BASELINES = new()
    {
        [ReasoningTier.NonThink] = 500,
        [ReasoningTier.ThinkHigh] = 4000,
        [ReasoningTier.ThinkMax] = 32000
    };

    private static readonly Dictionary<ReasoningTier, int> MIN_CONTEXT = new()
    {
        [ReasoningTier.NonThink] = 4096,
        [ReasoningTier.ThinkHigh] = 32768,
        [ReasoningTier.ThinkMax] = 131072
    };

    private const double CONTEXT_OVERHEAD_RATIO = 4.0;

    private static readonly Dictionary<ReasoningTier, (int deepProbeDepth, int selfPlayRounds, int aggregateModels, string modelTier)> TIER_STRATEGIES = new()
    {
        [ReasoningTier.NonThink] = (0, 0, 1, "flash"),
        [ReasoningTier.ThinkHigh] = (2, 2, 2, "think"),
        [ReasoningTier.ThinkMax] = (4, 4, 3, "max")
    };

    private static readonly HashSet<string> ComplexKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "prove", "implement", "design", "architect",
        "\u4e3a\u4ec0\u4e48", "\u5982\u4f55\u5b9e\u73b0", "\u8bbe\u8ba1"
    };

    private static readonly char[] ListMarkers = { '-', '*', '1', '2', '3', '4', '5', '6', '7', '8', '9', '\u2022' };

    private double _complexityEma = 0.3;
    private double _patienceEma = 5000;
    private readonly ConcurrentDictionary<ReasoningTier, int> _tierCounts = new();
    private Dictionary<ReasoningTier, int> _baselines;
    private readonly ILogger<ReasoningBudgetEngine> _logger;

    public ReasoningBudgetEngine(ILogger<ReasoningBudgetEngine> logger)
    {
        _logger = logger;
        _baselines = new Dictionary<ReasoningTier, int>(TIER_BASELINES);
    }

    public ReasoningBudget Allocate(
        string query,
        string taskType,
        int contextAvailable,
        double userPatienceMs,
        double conversationRhythm = 1.0,
        ReasoningTier? forceTier = null)
    {
        var (complexity, contextNeeded) = _EstimateComplexity(query, taskType);
        complexity = Math.Clamp(complexity, 0.05, 1.0);

        _complexityEma = _complexityEma * 0.85 + complexity * 0.15;
        _patienceEma = _patienceEma * 0.85 + userPatienceMs * 0.15;

        var tier = forceTier ?? SelectTier(complexity, userPatienceMs, contextAvailable);

        int baseTokens = _baselines[tier];
        double patienceFactor = _PatienceFactor(userPatienceMs);
        double contextFactor = _ContextFactor(contextAvailable, tier);
        int thinkingTokens = (int)(baseTokens * _complexityEma * patienceFactor * contextFactor);
        thinkingTokens = Math.Max(thinkingTokens, 100);

        int contextAllocated = (int)(thinkingTokens * CONTEXT_OVERHEAD_RATIO);
        int contextRemaining = contextAvailable - contextAllocated;

        if (contextAllocated > contextAvailable && tier != ReasoningTier.NonThink)
        {
            tier = DowngradeTier(tier);
            baseTokens = _baselines[tier];
            thinkingTokens = baseTokens;
            contextAllocated = (int)(thinkingTokens * CONTEXT_OVERHEAD_RATIO);
            contextRemaining = contextAvailable - contextAllocated;
            _logger.LogWarning(
                "Context insufficient ({Needed}>{Available}), downgraded to {Tier}",
                contextAllocated, contextAvailable, tier);
        }

        var (deepProbeDepth, selfPlayRounds, aggregateModels, modelTier) = TIER_STRATEGIES[tier];

        if (complexity > 0.8)
        {
            deepProbeDepth = Math.Min(deepProbeDepth + 2, 6);
            selfPlayRounds = Math.Min(selfPlayRounds + 1, 5);
            aggregateModels = Math.Min(aggregateModels + 1, 4);
        }

        double estimatedLatencyMs = thinkingTokens * 20.0;

        _tierCounts.AddOrUpdate(tier, 1, (_, c) => c + 1);

        return new ReasoningBudget
        {
            ThinkingTokens = thinkingTokens,
            Tier = tier,
            DeepProbeDepth = deepProbeDepth,
            SelfPlayRounds = selfPlayRounds,
            AggregateModels = aggregateModels,
            ModelTier = modelTier,
            ContextAvailable = contextAvailable,
            ContextAllocated = contextAllocated,
            ContextRemaining = contextRemaining,
            EstimatedLatencyMs = estimatedLatencyMs,
            UserPatienceMs = userPatienceMs,
            TaskComplexity = complexity,
            ConversationRhythm = conversationRhythm
        };
    }

    public void RecordActual(ReasoningBudget budget, int actualTokens)
    {
        budget.ActualTokensUsed = actualTokens;
        budget.BudgetEfficiency = budget.ThinkingTokens > 0
            ? Math.Clamp(actualTokens / (double)budget.ThinkingTokens, 0.1, 3.0)
            : 1.0;

        _logger.LogDebug(
            "Budget efficiency for {Tier}: {Efficiency:F2} (estimated={Est}, actual={Actual})",
            budget.Tier, budget.BudgetEfficiency, budget.ThinkingTokens, actualTokens);
    }

    public double GetEfficiencyForElo(ReasoningBudget budget)
    {
        double eff = budget.BudgetEfficiency;
        if (eff >= 0.9 && eff <= 1.1) return 0.05;
        if (eff >= 0.7 && eff <= 1.3) return 0.03;
        if (eff >= 0.5 && eff <= 1.5) return 0.01;
        if (eff >= 0.3 && eff <= 2.0) return -0.02;
        return -0.05;
    }

    public void AdaptThresholds()
    {
        foreach (var tier in new[] { ReasoningTier.NonThink, ReasoningTier.ThinkHigh, ReasoningTier.ThinkMax })
        {
            int current = _baselines[tier];
            int original = TIER_BASELINES[tier];

            int adjusted = (int)(current * 0.85 + original * 0.15);
            adjusted = Math.Clamp(adjusted, original / 4, original * 3);
            _baselines[tier] = adjusted;
        }

        _logger.LogInformation(
            "Adapted thresholds — NonThink={N}, ThinkHigh={H}, ThinkMax={M}",
            _baselines[ReasoningTier.NonThink],
            _baselines[ReasoningTier.ThinkHigh],
            _baselines[ReasoningTier.ThinkMax]);
    }

    private ReasoningTier SelectTier(double complexity, double patienceMs, int contextAvailable)
    {
        if (complexity < 0.25 || patienceMs < 1000)
            return ReasoningTier.NonThink;

        if (complexity > 0.7 && contextAvailable >= 196000 && patienceMs > 8000)
            return ReasoningTier.ThinkMax;

        if (complexity > 0.35 && patienceMs > 2000)
            return ReasoningTier.ThinkHigh;

        return ReasoningTier.NonThink;
    }

    private ReasoningTier DowngradeTier(ReasoningTier tier)
    {
        return tier switch
        {
            ReasoningTier.ThinkMax => ReasoningTier.ThinkHigh,
            ReasoningTier.ThinkHigh => ReasoningTier.NonThink,
            _ => ReasoningTier.NonThink
        };
    }

    private double _PatienceFactor(double ms)
    {
        if (ms < 1000) return 0.5;
        if (ms < 3000) return 0.8;
        if (ms > 15000) return 2.0;
        if (ms > 8000) return 1.5;
        return 1.0;
    }

    private double _ContextFactor(int context, ReasoningTier tier)
    {
        int minContext = MIN_CONTEXT.GetValueOrDefault(tier, 4096);
        double ratio = context / (CONTEXT_OVERHEAD_RATIO * minContext);
        return Math.Clamp(ratio, 0.3, 2.0);
    }

    private (double complexity, int needed) _EstimateComplexity(string query, string taskType)
    {
        double complexity = 0.2;
        int needed = 4096;

        int charCount = query.Length;
        if (charCount > 2000) complexity += 0.3;
        else if (charCount > 500) complexity += 0.15;
        else if (charCount > 100) complexity += 0.05;

        complexity += taskType.ToLowerInvariant() switch
        {
            "code_engineering" => 0.25,
            "architectural_design" => 0.30,
            "data_analysis" => 0.15,
            _ => 0.0
        };

        foreach (var keyword in ComplexKeywords)
        {
            if (query.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                complexity += 0.1;
                break;
            }
        }

        int listMarkerCount = 0;
        foreach (var line in query.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length > 0 && ListMarkers.Any(m => trimmed[0] == m))
                listMarkerCount++;
        }
        if (listMarkerCount >= 5) complexity += 0.15;
        else if (listMarkerCount >= 2) complexity += 0.05;

        needed = complexity switch
        {
            > 0.7 => 131072,
            > 0.35 => 32768,
            _ => 4096
        };

        return (complexity, needed);
    }
}
