using System.Text.RegularExpressions;

namespace LTAI.AI.Governors;

public sealed record LocalIntentScore
{
    public string Label { get; init; } = "deep";
    public float Confidence { get; init; }
    public float Complexity { get; init; }
}

public sealed class LocalIntentClassifier
{
    private static readonly (string Label, string[] PositiveKeywords, string[] NegativeKeywords)[] IntentRules =
    [
        new("fast", new[]
        {
            "你好", "hello", "hi", "谢谢", "thanks", "不错", "bye", "再见",
            "什么是", "what is", "who is", "when", "where",
            "怎么", "how to", "如何", "解释", "explain",
            "简单", "simple", "quick", "快速",
            "帮我", "help me", "帮我看下"
        },
        new[]
        {
            "分析", "设计", "架构", "优化", "重构", "规划", "为什么",
            "复杂", "深入", "详细", "对比", "评估"
        }),

        new("deep", new[]
        {
            "分析", "analyze", "审查", "review", "设计", "design",
            "架构", "architecture", "规划", "plan", "重构", "refactor",
            "优化", "optimize", "调试", "debug", "为什么", "why",
            "比较", "compare", "评估", "evaluate", "解释原理",
            "pipeline", "workflow", "流程", "编排", "orchestrate",
            "复杂", "complex", "深入", "detailed", "详细",
            "方案", "solution", "策略", "strategy"
        },
        new[]
        {
            "你好", "hello", "hi", "谢谢", "bye"
        }),
    ];

    public LocalIntentScore Classify(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new LocalIntentScore { Label = "deep", Confidence = 1.0f, Complexity = 0.5f };

        var trimmed = query.Trim();
        var lower = trimmed.ToLowerInvariant();

        if (IsSpinalCommand(trimmed))
            return new LocalIntentScore { Label = "reflex", Confidence = 1.0f, Complexity = 0.1f };

        var scores = new Dictionary<string, float>();
        var negativeScores = new Dictionary<string, float>();

        foreach (var (label, positives, negatives) in IntentRules)
        {
            float positiveScore = 0;
            foreach (var kw in positives)
            {
                if (lower.Contains(kw.ToLowerInvariant()))
                    positiveScore += 1.0f;
            }

            float negativeScore = 0;
            foreach (var kw in negatives)
            {
                if (lower.Contains(kw.ToLowerInvariant()))
                    negativeScore += 1.0f;
            }

            scores[label] = positiveScore;
            negativeScores[label] = negativeScore;
        }

        var totalPositive = scores.Values.Sum();
        if (totalPositive == 0)
            return HeuristicClassify(trimmed, lower);

        var bestLabel = scores.MaxBy(kv => kv.Value).Key;
        var bestScore = scores[bestLabel];
        var negativeForBest = negativeScores[bestLabel];

        var netScore = Math.Max(0, bestScore - negativeForBest);
        var confidence = netScore / (netScore + Math.Max(1, totalPositive - netScore));

        confidence = Math.Clamp(confidence, 0.1f, 0.95f);

        var complexity = ComputeComplexity(trimmed, lower);

        if (confidence < 0.3f)
            return HeuristicClassify(trimmed, lower);

        return new LocalIntentScore
        {
            Label = bestLabel,
            Confidence = confidence,
            Complexity = complexity
        };
    }

    private static LocalIntentScore HeuristicClassify(string trimmed, string lower)
    {
        var len = trimmed.Length;
        bool isCodeRelated = Regex.IsMatch(lower, @"(?:code|代码|function|class|def|bug|error|编译|compile|refactor|重构|implement|实现)");
        bool isLongForm = len > 200 || trimmed.Count(c => c == '\n') > 3;
        bool isMultiPart = Regex.IsMatch(lower, @"(?:首先.*然后|第一.*第二|步骤|step\s*\d|1\.\s.*2\.\s)");
        bool isSimple = Regex.IsMatch(lower, @"^(你好|hi|hello|谢谢|bye|再见|什么是|what is|how to|如何|怎么)") && len < 50;

        if (isSimple && len < 50)
        {
            var complexity = 0.2f + Math.Min(len / 500f, 0.3f);
            return new LocalIntentScore
            {
                Label = complexity > 0.4f ? "deep" : "fast",
                Confidence = 0.4f,
                Complexity = complexity
            };
        }

        if (isLongForm || isMultiPart)
            return new LocalIntentScore { Label = "deep", Confidence = 0.5f, Complexity = Math.Min(0.6f + len / 2000f, 1.0f) };

        if (isCodeRelated)
            return new LocalIntentScore { Label = "deep", Confidence = 0.45f, Complexity = 0.5f + Math.Min(len / 1000f, 0.5f) };

        var defaultComplexity = 0.3f + Math.Min(len / 1500f, 0.5f);
        return new LocalIntentScore
        {
            Label = defaultComplexity > 0.5f ? "deep" : "fast",
            Confidence = 0.35f,
            Complexity = defaultComplexity
        };
    }

    private static float ComputeComplexity(string trimmed, string lower)
    {
        float score = 0.3f;

        score += Math.Min(trimmed.Length / 1000f, 0.3f);
        score += Math.Min(trimmed.Count(c => c == '\n') * 0.05f, 0.2f);

        var questionMarks = trimmed.Count(c => c is '?' or '？');
        score += Math.Min(questionMarks * 0.05f, 0.15f);

        var complexityWords = new[] { "复杂", "深入", "详细", "分析", "设计", "架构", "为什么", "如何", "方案", "策略" };
        foreach (var w in complexityWords)
            if (lower.Contains(w)) score += 0.1f;

        return Math.Clamp(score, 0.1f, 1.0f);
    }

    private static bool IsSpinalCommand(string query)
    {
        var commands = new[] { "/help", "/status", "/pause", "/resume", "/restart" };
        return commands.Any(cmd => query.StartsWith(cmd, StringComparison.OrdinalIgnoreCase));
    }
}
