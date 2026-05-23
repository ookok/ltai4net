using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public enum MoEExpert
{
    Code, Math, Chat, Reasoning, EIA, General
}

public record MoERouteResult
{
    public MoEExpert Primary { get; init; }
    public MoEExpert? Secondary { get; init; }
    public float PrimaryScore { get; init; }
    public float SecondaryScore { get; init; }
    public HrmReasoningTier RecommendedTier { get; init; }
    public string Reason { get; init; } = "";
}

/// Mixture-of-Experts router: maps queries to domain experts.
/// Each expert has its own LoRA adapter + optionally a dedicated small model.
/// Only top-2 experts are activated per query (sparse activation).
public sealed class MoERouter
{
    private readonly AdaptiveDepthController _depthController;
    private readonly ILogger<MoERouter> _logger;
    private readonly Dictionary<MoEExpert, int> _expertHits = new();

    // Expert keyword profiles (Chinese + English)
    private static readonly Dictionary<MoEExpert, string[]> ExpertKeywords = new()
    {
        [MoEExpert.Code] = new[] { "code", "programming", "function", "class", "debug",
            "build", "test", "refactor", "代码", "函数", "编程", "调试", "bug", "api",
            "import", "async", "await", "public", "private", "return" },
        [MoEExpert.Math] = new[] { "math", "calculate", "formula", "equation", "prove",
            "数学", "计算", "公式", "方程", "证明", "statistics", "统计", "probability",
            "algebra", "geometry" },
        [MoEExpert.Chat] = new[] { "hello", "hi", "thanks", "help", "what is",
            "how are", "你好", "谢谢", "什么是", "介绍一下", "tell me about" },
        [MoEExpert.Reasoning] = new[] { "analyze", "reason", "think", "compare",
            "evaluate", "solve", "logic", "为什么", "如何", "分析", "推理", "比较",
            "评估", "explain" },
        [MoEExpert.EIA] = new[] { "环境", "impact", "emission", "environmental", "gis",
            "map", "spatial", "ecological", "环评", "污染", "生态", "排放",
            "carbon", "碳", "sustainability" }
    };

    public MoERouter(AdaptiveDepthController depthController,
        ILogger<MoERouter>? logger = null)
    {
        _depthController = depthController;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MoERouter>.Instance;
    }

    public MoERouteResult Route(string query)
    {
        var lower = query.ToLowerInvariant();
        var scores = new Dictionary<MoEExpert, float>();

        foreach (var (expert, keywords) in ExpertKeywords)
        {
            var hits = keywords.Count(k => lower.Contains(k));
            var total = keywords.Length;
            scores[expert] = total > 0 ? Math.Min(1f, (float)hits / total * 5f) : 0f;
        }

        // Boost with depth controller complexity
        var complexity = _depthController.CalculateComplexity(query);
        if (complexity > 0.5f) scores[MoEExpert.Reasoning] += 0.2f;
        if (complexity > 0.7f) scores[MoEExpert.Math] += 0.15f;

        var sorted = scores.OrderByDescending(kv => kv.Value).ToList();
        var primary = sorted[0].Key;
        var primaryScore = sorted[0].Value;
        var secondary = sorted.Count > 1 && sorted[1].Value > 0.1f ? sorted[1].Key : (MoEExpert?)null;
        var secondaryScore = sorted.Count > 1 ? sorted[1].Value : 0f;

        // If no strong signal, fall back to General
        if (primaryScore < 0.1f)
        {
            primary = MoEExpert.General;
            secondary = null;
        }

        var tier = primary switch
        {
            MoEExpert.Code => HrmReasoningTier.DeepThink,
            MoEExpert.Math => HrmReasoningTier.DeepThink,
            MoEExpert.Reasoning => HrmReasoningTier.FullReason,
            MoEExpert.Chat => HrmReasoningTier.FastThink,
            MoEExpert.EIA => HrmReasoningTier.FullReason,
            _ => complexity > 0.4f ? HrmReasoningTier.DeepThink : HrmReasoningTier.FastThink
        };

        lock (_expertHits)
        {
            if (!_expertHits.ContainsKey(primary))
                _expertHits[primary] = 0;
            _expertHits[primary]++;
        }

        var route = new MoERouteResult
        {
            Primary = primary, Secondary = secondary,
            PrimaryScore = primaryScore, SecondaryScore = secondaryScore,
            RecommendedTier = tier,
            Reason = $"keywords={sorted[0].Value:F2}; complexity={complexity:F2}"
        };

        _logger.LogDebug("MoE route: {Primary}({Pscore:F2}) [{Secondary}] → {Tier}",
            primary, primaryScore, secondary, tier);

        return route;
    }

    /// Get the current model name for an expert (used to select LoRA/tiny model)
    public string GetExpertModelName(MoEExpert expert)
    {
        return expert switch
        {
            MoEExpert.Code => "l1_code_lora",
            MoEExpert.Math => "l1_math_validator",
            MoEExpert.Chat => "l1_fast_lora",
            MoEExpert.Reasoning => "l1_deep_lora",
            MoEExpert.EIA => "l2_knowledge_retrieval",
            _ => "l1_fast_lora"
        };
    }

    public Dictionary<string, object> GetStats()
    {
        return new Dictionary<string, object>
        {
            ["expert_distribution"] = _expertHits.ToDictionary(kv => kv.Key.ToString(), kv => (object)kv.Value)
        };
    }
}

/// Model Soup: average weights of multiple LoRA checkpoints for zero-cost ensembling.
/// Paper: Wortsman et al. 2022, "Model soups: averaging weights of multiple
/// fine-tuned models improves accuracy without increasing inference time."
public static class ModelSoup
{
    /// Average two LoRA layers. Returns a new merged LoraCheckpoint.
    public static LoraCheckpoint Average(LoraCheckpoint a, LoraCheckpoint b, float weightA = 0.5f)
    {
        var wB = 1f - weightA;
        var rows = Math.Min(a.A.GetLength(0), b.A.GetLength(0));
        var rank = Math.Min(a.A.GetLength(1), b.A.GetLength(1));
        var cols = Math.Min(a.B.GetLength(1), b.B.GetLength(1));

        var avgA = new float[rows, rank];
        var avgB = new float[rank, cols];

        for (int i = 0; i < rows; i++)
        for (int j = 0; j < rank; j++)
            avgA[i, j] = a.A[i, j] * weightA + b.A[i, j] * wB;

        for (int i = 0; i < rank; i++)
        for (int j = 0; j < cols; j++)
            avgB[i, j] = a.B[i, j] * weightA + b.B[i, j] * wB;

        return new LoraCheckpoint
        {
            InputDim = Math.Min(a.InputDim, b.InputDim),
            OutputDim = Math.Min(a.OutputDim, b.OutputDim),
            Rank = rank, Scale = (a.Scale + b.Scale) / 2f,
            A = avgA, B = avgB
        };
    }

    /// Average multiple checkpoints via iterative pairwise averaging.
    public static LoraCheckpoint AverageMany(List<LoraCheckpoint> checkpoints)
    {
        if (checkpoints.Count == 0)
            throw new ArgumentException("No checkpoints to average");
        if (checkpoints.Count == 1) return checkpoints[0];

        var result = checkpoints[0];
        for (int i = 1; i < checkpoints.Count; i++)
            result = Average(result, checkpoints[i], 1f / (i + 1));

        return result;
    }

    /// Uniform averaging across all checkpoints.
    public static LoraCheckpoint UniformAverage(List<LoraCheckpoint> checkpoints)
    {
        if (checkpoints.Count == 0)
            throw new ArgumentException("No checkpoints to average");

        var first = checkpoints[0];
        var rows = first.A.GetLength(0);
        var rank = first.A.GetLength(1);
        var cols = first.B.GetLength(1);
        int n = checkpoints.Count;

        var avgA = new float[rows, rank];
        var avgB = new float[rank, cols];
        float avgScale = 0;

        foreach (var ckpt in checkpoints)
        {
            for (int i = 0; i < rows; i++)
            for (int j = 0; j < rank; j++)
                avgA[i, j] += ckpt.A[i, j] / n;

            for (int i = 0; i < rank; i++)
            for (int j = 0; j < cols; j++)
                avgB[i, j] += ckpt.B[i, j] / n;

            avgScale += ckpt.Scale / n;
        }

        return new LoraCheckpoint
        {
            InputDim = first.InputDim, OutputDim = first.OutputDim,
            Rank = rank, Scale = avgScale, A = avgA, B = avgB
        };
    }
}
