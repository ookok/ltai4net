using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public enum HrmReasoningTier
{
    /// <summary>
    /// Fast reasoning — L1 flash model. Handles simple to moderate queries.
    /// Merged from previous Reflex, FastThink, DeepThink.
    /// </summary>
    Fast = 0,
    /// <summary>
    /// Deep reasoning — L2 pro model. For high complexity / code / creative.
    /// </summary>
    Deep = 1
}

public sealed record DepthDecision
{
    public HrmReasoningTier Tier { get; init; }
    public float Complexity { get; init; }
    public float Confidence { get; init; }
    public string RecommendedModel { get; init; } = "l1_fast";
    public int RecommendedRank { get; init; }
    public int MaxRecursionDepth { get; init; }
    public int ThinkingTokenBudget { get; init; }
    public bool IsHard { get; init; }
    public string Reason { get; init; } = "";
    public CollaborationPattern Pattern { get; init; } = CollaborationPattern.Sequential;
}

/// Unified adaptive depth controller for HRM (Hierarchical Reasoning Model).
/// Consolidates 4 previously separate components:
///   ReasoningBudget + TokenHardnessDecider + ComplexityCalculator + CollaborationSelector
/// into a single decision engine with tier-optimized LoRA routing.
public sealed class AdaptiveDepthController
{
    private readonly ILogger<AdaptiveDepthController> _logger;
    private readonly LearningProgressTracker? _paceTracker;

    // EMA trackers for smooth adaptation
    private float _complexityEma = 0.3f;
    private float _patienceEma = 5000f;
    private int _totalDecisions;
    private int _escalationCount;
    private readonly Dictionary<HrmReasoningTier, int> _tierCounts = new()
    {
        [HrmReasoningTier.Fast] = 0, [HrmReasoningTier.Deep] = 0
    };

    // HölderPO p-annealing (arXiv:2605.12058)
    private float _holderP = 3.0f;
    private int _totalTrainingSteps;

    /// Current Hölder p (3.0→1.0 over training lifecycle).
    /// p>1: amplifies hard samples, fast learning. p<1: stable convergence.
    public float CurrentHolderP => _holderP;

    // Tier baselines: (thinking tokens, max recursion, rank, model name)
    // L0 Reflex has been merged into L1 Fast; Escalate removed.
    private static readonly Dictionary<HrmReasoningTier, (int tokens, int recursion, int rank, string model)> TierConfig = new()
    {
        [HrmReasoningTier.Fast] = (2000, 2, 8, "l1_fast"),
        [HrmReasoningTier.Deep] = (8000, 3, 0, "l2_full")
    };

    // Complexity keywords (cross-lingual)
    private static readonly HashSet<string> ComplexKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "prove", "implement", "design", "architect", "optimize", "refactor",
        "debug", "为什么", "如何实现", "设计", "架构", "优化", "重构", "调试",
        "analyze", "compare", "evaluate", "benchmark", "migrate", "deploy"
    };

    private static readonly string[] CodeMarkers = { "```", "using ", "import ", "class ", "function ",
        "def ", "package ", "module ", "require(", "public ", "private " };

    private static readonly string[] ReasoningMarkers = { "why", "how", "explain", "analyze",
        "为什么", "如何", "解释", "分析", "原因", "compare", "区别" };

    private static readonly string[] PlanningMarkers = { "首先", "然后", "接着", "最后",
        "step", "first", "then", "finally", "1.", "2.", "3.", "plan", "todo" };

    private static readonly string[] DomainKeywords = { "architecture", "algorithm", "concurrency",
        "distributed", "security", "encryption", "神经网络", "transformer", "gradient",
        "架构", "算法", "并发", "分布式", "安全", "加密" };

    public AdaptiveDepthController(
        ILogger<AdaptiveDepthController>? logger = null,
        LearningProgressTracker? paceTracker = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AdaptiveDepthController>.Instance;
        _paceTracker = paceTracker;
    }

    public DepthDecision Decide(string query, int contextAvailable = 4096,
        double userPatienceMs = 5000, HrmReasoningTier? forceTier = null)
    {
        var complexity = CalculateComplexity(query);
        _complexityEma = _complexityEma * 0.85f + complexity * 0.15f;
        _patienceEma = _patienceEma * 0.85f + (float)userPatienceMs * 0.15f;

        // Determine hardness via token-level difficulty heuristics
        var isHard = complexity > 0.55f || IsIntrinsicallyHard(query);

        // Select tier (binary: Fast[L1] vs Deep[L2])
        var tier = forceTier ?? SelectTier(complexity, userPatienceMs, contextAvailable);

        // Get tier config
        var (tokens, recursion, rank, model) = TierConfig[tier];

        // Adjust based on PACE tracking — if converged, boost to Deep
        var isPlateau = _paceTracker is not null && _paceTracker.IsConverged("global", minUpdates: 5);
        if (isPlateau && tier == HrmReasoningTier.Fast)
            tier = HrmReasoningTier.Deep;

        var (adjTokens, adjRank) = AdjustForContext(tier, tokens, rank, contextAvailable);

        var pattern = SelectCollaborationPattern(query, complexity);

        lock (_tierCounts) { _tierCounts[tier] = _tierCounts.GetValueOrDefault(tier) + 1; }
        _totalDecisions++;
        if (tier == HrmReasoningTier.Deep) _escalationCount++;

        var decision = new DepthDecision
        {
            Tier = tier, Complexity = complexity,
            Confidence = 1.0f - complexity * 0.5f,
            RecommendedModel = model, RecommendedRank = adjRank,
            MaxRecursionDepth = recursion, ThinkingTokenBudget = adjTokens,
            IsHard = isHard, Pattern = pattern,
            Reason = BuildReason(tier, complexity, isHard)
        };

        _logger.LogDebug(
            "DepthDecision: tier={Tier} complexity={Complexity:F3} rank={Rank} tokens={Tokens} pattern={Pattern}",
            tier, complexity, adjRank, adjTokens, pattern);

        return decision;
    }

    public float CalculateComplexity(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return 0.1f;

        var lower = query.ToLowerInvariant();
        float score = 0.1f;

        // 1. Length factor
        score += Math.Min(0.2f, query.Length / 2000f * 0.2f);

        // 2. Code detection
        var codeScore = CodeMarkers.Count(m => lower.Contains(m));
        score += Math.Min(0.2f, codeScore * 0.05f);

        // 3. Constraint density (条件/限制词)
        var constraints = global::System.Text.RegularExpressions.Regex.Matches(query,
            @"必须|不要|除了|except|only|without|if|when|应该|should|must");
        score += Math.Min(0.25f, constraints.Count * 0.08f);

        // 4. Logic connectors
        var logicCount = global::System.Text.RegularExpressions.Regex.Matches(query,
            @"因为|所以|虽然|但是|because|therefore|however|导致|影响|implies");
        score += Math.Min(0.2f, logicCount.Count * 0.07f);

        // 5. Multi-step sequencing
        var steps = PlanningMarkers.Count(m => lower.Contains(m));
        score += Math.Min(0.15f, steps * 0.05f);

        // 6. Domain-specific keywords
        if (DomainKeywords.Any(k => lower.Contains(k))) score += 0.1f;

        // 7. Reasoning indicators
        if (ReasoningMarkers.Any(k => lower.Contains(k))) score += 0.08f;

        // 8. Entity density
        var entities = global::System.Text.RegularExpressions.Regex.Matches(query,
            @"[A-Z][a-z]{2,}|[0-9]+|[\u4e00-\u9fa5]{2,}").Count;
        score += Math.Min(0.15f, entities * 0.03f);

        return Math.Clamp(score, 0.05f, 1.0f);
    }

    private static bool IsIntrinsicallyHard(string query)
    {
        var lower = query.ToLowerInvariant();
        return query.Length > 500 ||
               ComplexKeywords.Any(k => lower.Contains(k)) ||
               CodeMarkers.Count(m => lower.Contains(m)) >= 2 ||
               global::System.Text.RegularExpressions.Regex.Matches(query, @"\n\s*(?:\d+[\.\)]|[-•])").Count >= 2;
    }

    private HrmReasoningTier SelectTier(float complexity, double patienceMs, int contextAvailable)
    {
        // Binary decision: Fast (L1 flash) for simple queries, Deep (L2 pro) for complex ones
        // Thresholds calibrated to match previous Reflex+FastThink+DeepThink vs FullReason
        if (complexity < 0.55f && patienceMs < 5000)
            return HrmReasoningTier.Fast;

        // High complexity or adequate patience/capacity → Deep
        return HrmReasoningTier.Deep;
    }

    private (int tokens, int rank) AdjustForContext(HrmReasoningTier tier, int baseTokens, int baseRank, int contextAvailable)
    {
        var maxTokens = (int)(contextAvailable / 4.0); // context overhead ratio
        var tokens = Math.Min(baseTokens, maxTokens);
        tokens = Math.Max(tokens, 100);

        // Reduce rank if context is very limited
        var rank = contextAvailable < 2048 && baseRank > 4 ? baseRank / 2 : baseRank;

        return (tokens, rank);
    }

    private static CollaborationPattern SelectCollaborationPattern(string query, float complexity)
    {
        var lower = query.ToLowerInvariant();

        if (lower.Contains("code") || lower.Contains("代码") || lower.Contains("debug") ||
            lower.Contains("bug") || lower.Contains("refactor") || lower.Contains("test"))
            return CollaborationPattern.Sequential;

        if (complexity > 0.7f && (lower.Contains("design") || lower.Contains("设计") ||
            lower.Contains("architecture") || lower.Contains("架构")))
            return CollaborationPattern.Mixture;

        if (lower.Contains("explain") || lower.Contains("teach") || lower.Contains("解释") ||
            lower.Contains("简化"))
            return CollaborationPattern.Distillation;

        if (lower.Contains("verify") || lower.Contains("check") || lower.Contains("验证") ||
            lower.Contains("审查"))
            return CollaborationPattern.Deliberation;

        return CollaborationPattern.Sequential;
    }

    private static string BuildReason(HrmReasoningTier tier, float complexity, bool isHard)
    {
        var parts = new List<string> { $"complexity={complexity:F2}" };
        if (isHard) parts.Add("hard_query");
        parts.Add(tier switch
        {
            HrmReasoningTier.Fast => "l1_fast_flash",
            HrmReasoningTier.Deep => "l2_full_pro",
            _ => "unknown"
        });
        return string.Join("; ", parts);
    }

    public Dictionary<string, object> GetStats()
    {
        return new Dictionary<string, object>
        {
            ["total_decisions"] = _totalDecisions,
            ["complexity_ema"] = _complexityEma,
            ["escalation_rate"] = _totalDecisions > 0 ? (float)_escalationCount / _totalDecisions : 0,
            ["tier_distribution"] = _tierCounts.ToDictionary(
                kv => kv.Key.ToString(), kv => (object)kv.Value),
            ["plateau"] = _paceTracker?.IsConverged("global", 5) ?? false,
            ["holder_p"] = _holderP,
            ["training_steps"] = _totalTrainingSteps
        };
    }

    /// HölderPO p-annealing: p 从 3.0 线性退火到 1.0
    /// 训练初期 p=3 (梯度集中→快速学习稀疏信号)
    /// 训练后期 p=1 (算术平均→稳定收敛)
    public float StepPAnnealing(int totalSteps = 200)
    {
        _totalTrainingSteps++;
        var progress = Math.Clamp((float)_totalTrainingSteps / totalSteps, 0f, 1f);
        _holderP = 3.0f - 2.0f * progress;
        _logger.LogDebug("HölderPO p-annealing: step={Step}/{Total} p={P:F2} progress={Prog:F0}%",
            _totalTrainingSteps, totalSteps, _holderP, progress * 100);
        return _holderP;
    }

    /// Reset p to initial aggressive value (new training cycle)
    public void ResetHolderP() { _holderP = 3.0f; _totalTrainingSteps = 0; }
}
