using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Routing;

public sealed class UnifiedRoute
{
    public string Intent { get; init; } = "chat";
    public string TargetAgent { get; init; } = "chat";
    public float Confidence { get; init; } = 1.0f;
    public List<string> MatchedKeywords { get; init; } = new();
    public string Source { get; init; } = "unified";
    public bool UseWorkflow { get; init; }
    public string? QueryShape { get; init; }
}

public sealed class UnifiedIntentRouter
{
    private readonly ILogger<UnifiedIntentRouter> _logger;
    private readonly IntentRouter _intentRouter;

    private static readonly (string Shape, string[] Patterns, string PreferredIntent)[] QueryShapes =
    {
        ("ExactLookup", new[] { "what is", "define", "show me", "什么是", "定义" }, "chat"),
        ("PolicyVersioned", new[] { "gb ", "hj ", "标准", "standard", "regulation", "法规" }, "eia"),
        ("SemanticConcept", new[] { "explain", "how does", "why", "concept", "解释", "为什么", "如何" }, "reasoning"),
        ("MultiHop", new[] { "compare", "difference between", "pros and cons", "对比", "比较", "优劣" }, "reasoning"),
        ("ComparativeAnalysis", new[] { "which is better", "evaluate", "assess", "哪个更好", "评估" }, "reasoning"),
        ("TemporalQuery", new[] { "when", "history", "timeline", "什么时候", "历史", "时间线" }, "chat"),
        ("SpatialQuery", new[] { "where", "location", "gis", "map", "spatial", "位置", "地图", "空间" }, "eia"),
        ("NumericCalculation", new[] { "calculate", "compute", "formula", "计算", "公式", "模型" }, "eia"),
        ("AggregationSummary", new[] { "summarize", "overview", "summary", "总结", "概述", "概要" }, "chat"),
        ("ProceduralHowTo", new[] { "how to", "steps", "tutorial", "guide", "怎么做", "步骤", "教程" }, "code"),
        ("CodeGeneration", new[] { "write code", "implement", "generate code", "写代码", "实现", "生成代码" }, "code"),
    };

    private static readonly string[] WorkflowKeywords =
    {
        "analyze", "review", "design", "architecture", "plan", "refactor", "debug",
        "investigate", "audit", "evaluate", "complex",
        "分析", "审查", "设计", "架构", "规划", "重构", "调试", "审计", "评估"
    };

    public UnifiedIntentRouter(ILogger<UnifiedIntentRouter> logger, IntentRouter intentRouter)
    {
        _logger = logger;
        _intentRouter = intentRouter;
    }

    public UnifiedRoute Route(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new UnifiedRoute { Intent = "chat", TargetAgent = "chat", Confidence = 1.0f };

        var intentRoute = _intentRouter.Classify(text);
        var queryShape = DetectQueryShape(text);
        var useWorkflow = ShouldUseWorkflow(text);

        var finalIntent = intentRoute.Intent;
        var finalAgent = intentRoute.TargetAgent;
        var finalConfidence = intentRoute.Confidence;

        if (queryShape != null && finalConfidence < 0.5f)
        {
            finalIntent = queryShape.Value.PreferredIntent;
            finalAgent = queryShape.Value.PreferredIntent;
            finalConfidence = 0.6f;
        }

        _logger.LogDebug("UnifiedRouter: intent={Intent} agent={Agent} conf={Conf:F2} shape={Shape} workflow={Workflow}",
            finalIntent, finalAgent, finalConfidence, queryShape?.Shape ?? "none", useWorkflow);

        return new UnifiedRoute
        {
            Intent = finalIntent,
            TargetAgent = finalAgent,
            Confidence = finalConfidence,
            MatchedKeywords = intentRoute.MatchedKeywords,
            Source = "unified",
            UseWorkflow = useWorkflow,
            QueryShape = queryShape?.Shape
        };
    }

    public IReadOnlyList<UnifiedRoute> RouteAll(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new[] { new UnifiedRoute { Intent = "chat", TargetAgent = "chat", Confidence = 1.0f } };

        var intentRoutes = _intentRouter.ClassifyAll(text);
        var queryShape = DetectQueryShape(text);
        var useWorkflow = ShouldUseWorkflow(text);

        return intentRoutes.Select(r => new UnifiedRoute
        {
            Intent = r.Intent,
            TargetAgent = r.TargetAgent,
            Confidence = r.Confidence,
            MatchedKeywords = r.MatchedKeywords,
            Source = "unified",
            UseWorkflow = useWorkflow,
            QueryShape = queryShape?.Shape
        }).ToList();
    }

    private static (string Shape, string PreferredIntent)? DetectQueryShape(string text)
    {
        var lower = text.ToLowerInvariant();
        foreach (var (shape, patterns, preferredIntent) in QueryShapes)
        {
            if (patterns.Any(p => lower.Contains(p)))
                return (shape, preferredIntent);
        }
        return null;
    }

    private static bool ShouldUseWorkflow(string text)
    {
        var lower = text.ToLowerInvariant();
        var keywordHits = WorkflowKeywords.Count(kw => lower.Contains(kw));
        if (keywordHits >= 2) return true;

        var wordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount > 50) return true;

        var sentenceCount = text.Count(c => c == '.' || c == '!' || c == '?' || c == '。' || c == '！' || c == '？');
        if (sentenceCount >= 3) return true;

        return false;
    }
}
