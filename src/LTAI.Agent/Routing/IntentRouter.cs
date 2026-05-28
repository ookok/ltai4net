using LTAI.Models;

namespace LTAI.Agent.Routing;

public sealed class IntentRoute
{
    public AgentType Intent { get; init; } = AgentType.Chat;
    public AgentType TargetAgent { get; init; } = AgentType.Chat;
    public float Confidence { get; init; } = 1.0f;
    public List<string> MatchedKeywords { get; init; } = new();
}

[Obsolete("Use HybridIntentRouter (LTAI.AI.Governors) instead. IntentRouter is the old 5-agent router " +
    "that will be removed in a future release. HybridIntentRouter provides fast/deep classification " +
    "with L0 embedding fallback and is registered in DI.")]
public sealed class IntentRouter
{
    private static readonly IntentRouteDefinition[] _routes =
    {
        new(AgentType.Code, AgentType.Code, 0.9f, new[]
        {
            "code", "programming", "class ", "function ", "debug", "build",
            "test", "refactor", "git", "compile", "lint", "syntax", "bug",
            "error ", "exception", "dependency", "package", "import ", "require "
        }),
        new(AgentType.EIA, AgentType.EIA, 0.9f, new[]
        {
            "环境", "impact", "emission", "environmental", "gis", "map",
            "spatial", "ecological", "dispersion", "plume", "noise", "water quality",
            "air quality", "carbon", "温室", "排放", "生态", "污染", "噪声",
            "aermod", "calpuff", "环境影响", "环评"
        }),
        new(AgentType.EiaCritic, AgentType.EiaCritic, 0.85f, new[]
        {
            "审查报告", "审核", "合规", "compliance check", "report review",
            "eia review", "报告审核", "标准引用"
        }),
        new(AgentType.Reasoning, AgentType.Reasoning, 0.85f, new[]
        {
            "analyze", "reason", "think", "compare", "evaluate", "solve",
            "logic", "为什么", "如何", "explain", "分析", "推理",
            "plan", "design", "architecture", "规划", "架构"
        }),
        new(AgentType.Chat, AgentType.Chat, 0.7f, new[]
        {
            "help", "hello", "hi", "chat", "talk", "conversation",
            "what is", "who is", "tell me", "你好", "谢谢"
        })
    };

    public IntentRoute Classify(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new IntentRoute { Intent = AgentType.Chat, TargetAgent = AgentType.Chat, Confidence = 1.0f };

        var lower = text.ToLowerInvariant();
        var bestScore = 0.0;
        IntentRouteDefinition? bestRoute = null;
        var allMatched = new List<string>();

        foreach (var route in _routes)
        {
            var matched = new List<string>();
            foreach (var kw in route.Keywords)
            {
                if (lower.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    matched.Add(kw);
            }

            if (matched.Count > 0)
            {
                allMatched.AddRange(matched);
                var score = matched.Count * route.BaseConfidence / route.Keywords.Length;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestRoute = route;
                }
            }
        }

        if (bestRoute is null)
            return new IntentRoute { Intent = AgentType.Chat, TargetAgent = AgentType.Chat, Confidence = 0.5f, MatchedKeywords = new() };

        var confidence = (float)Math.Min(bestRoute.BaseConfidence + bestScore * 0.1, 1.0);

        return new IntentRoute
        {
            Intent = bestRoute.Intent,
            TargetAgent = bestRoute.TargetAgent,
            Confidence = confidence,
            MatchedKeywords = allMatched.Distinct().ToList()
        };
    }

    public IReadOnlyList<IntentRoute> ClassifyAll(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new[] { new IntentRoute { Intent = AgentType.Chat, TargetAgent = AgentType.Chat, Confidence = 1.0f } };

        var lower = text.ToLowerInvariant();
        var results = new List<IntentRoute>();

        foreach (var route in _routes)
        {
            var matched = new List<string>();
            foreach (var kw in route.Keywords)
            {
                if (lower.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    matched.Add(kw);
            }

            if (matched.Count > 0)
            {
                var score = matched.Count * route.BaseConfidence / route.Keywords.Length;
                results.Add(new IntentRoute
                {
                    Intent = route.Intent,
                    TargetAgent = route.TargetAgent,
                    Confidence = Math.Min(route.BaseConfidence + score * 0.1f, 1.0f),
                    MatchedKeywords = matched
                });
            }
        }

        if (results.Count == 0)
            results.Add(new IntentRoute { Intent = AgentType.Chat, TargetAgent = AgentType.Chat, Confidence = 0.5f });

        return results.OrderByDescending(r => r.Confidence).ToList();
    }

    private sealed record IntentRouteDefinition(
        AgentType Intent,
        AgentType TargetAgent,
        float BaseConfidence,
        string[] Keywords);
}
