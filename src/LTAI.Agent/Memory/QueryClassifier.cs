using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Memory;

/// <summary>
/// Unified query classification service. Consolidates greeting detection,
/// query intent classification, and knowledge-query gating into a single
/// service registered as a DI singleton.
///
/// Callers:
///   - AgentWorkflows (greeting fast-path skip)
///   - RagContextStep (intent-aware graph traversal)
///   - ExpertRouterAgent (knowledge-query gating)
/// </summary>
public sealed class QueryClassifier
{
    private readonly IntentRouter _intentRouter;
    private readonly ILogger<QueryClassifier> _logger;

    private static readonly HashSet<string> GreetingsSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "hello", "hi", "hey", "你好", "嗨", "早上好", "下午好", "晚上好",
        "good morning", "good afternoon", "good evening",
        "who are you", "你是谁", "help", "帮助", "/help",
        "status", "状态", "/status", "thanks", "谢谢", "thank you",
        "こんにちは", "안녕하세요", "bonjour", "hola", "привет",
    };

    private static readonly string[] ToolKeywords =
    [
        "搜索", "查找", "写", "读", "删除", "创建", "执行", "运行", "计算", "分析", "翻译", "总结",
        "search", "find", "write", "read", "delete", "create", "execute", "run", "compute", "analyze", "translate", "summarize",
    ];

    private static readonly int _greetingMaxLength = int.TryParse(
        Environment.GetEnvironmentVariable("LTAI_GREETING_MAX_LENGTH"), out var g) ? Math.Max(3, g) : 15;

    public QueryClassifier(IntentRouter intentRouter, ILogger<QueryClassifier>? logger = null)
    {
        _intentRouter = intentRouter;
        _logger = logger ?? NullLogger<QueryClassifier>.Instance;
    }

    /// <summary>
    /// Classify the query intent using FastEmb centroid-based classification.
    /// Delegates to <see cref="IntentRouter.ClassifyWithScore"/>.
    /// </summary>
    public QueryIntent ClassifyIntent(string query) => _intentRouter.Classify(query);

    /// <summary>
    /// Classify the query intent with a confidence score.
    /// </summary>
    public (QueryIntent Intent, float Confidence) ClassifyIntentWithScore(string query)
        => _intentRouter.ClassifyWithScore(query);

    /// <summary>
    /// Detect whether the input is a pure greeting (no substantive request).
    /// Delegates to the shared static implementation.
    /// </summary>
    public bool IsGreetingOnly(string task) => IsGreetingOnlyStatic(task);

    /// <summary>
    /// Detect casual/simple queries that don't need heavy provider context.
    /// Examples: "ok", "thanks", "继续", "what's next?", "go on", "yes", "no".
    /// These are short follow-ups or acknowledgments, not substantive requests.
    /// </summary>
    public bool IsCasualQuery(string task)
    {
        if (string.IsNullOrWhiteSpace(task)) return false;
        var trimmed = task.Trim();

        // Already handled by greeting detection — don't double-match
        if (IsGreetingOnlyStatic(trimmed)) return false;

        // Very short queries (< 20 chars) with no tool keywords are likely casual
        if (trimmed.Length <= 20)
        {
            foreach (var keyword in ToolKeywords)
                if (trimmed.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return false;
            return true;
        }

        // Known casual patterns
        var lower = trimmed.ToLowerInvariant();
        var casualPatterns = new[]
        {
            "what's next", "what next", "go on", "continue", "继续",
            "yes", "no", "ok", "okay", "sure", "好的", "行",
            "tell me more", "接着说", "然后呢", "还有吗",
            "i see", "明白了", "懂了", "got it",
            "can you explain", "请解释", "什么意思",
            "what does that mean", "why", "为什么",
            "how does it work", "怎么用",
        };
        if (casualPatterns.Any(p => lower.Contains(p)))
            return true;

        return false;
    }

    /// <summary>
    /// Static fast-path greeting check (no DI needed). Used by callers that
    /// cannot inject QueryClassifier (e.g. static helper methods, ExpertRouterAgent).
    /// </summary>
    public static bool IsGreetingOnlyStatic(string task)
    {
        if (string.IsNullOrWhiteSpace(task)) return false;
        var trimmed = task.Trim();

        if (GreetingsSet.Contains(trimmed))
            return true;

        foreach (var keyword in ToolKeywords)
            if (trimmed.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return false;

        if (trimmed.Length <= _greetingMaxLength)
            return true;

        return false;
    }

    /// <summary>
    /// Full classification result combining greeting check and intent classification.
    /// </summary>
    public QueryClassResult Classify(string query)
    {
        var isGreeting = IsGreetingOnly(query);
        var (intent, confidence) = ClassifyIntentWithScore(query);

        return new QueryClassResult(isGreeting, intent, confidence);
    }
}

/// <summary>
/// Result of a full query classification pass.
/// </summary>
public readonly record struct QueryClassResult(
    bool IsGreeting,
    QueryIntent Intent,
    float Confidence)
{
    public bool IsSubstantive => !IsGreeting;
    public bool IsConfident => Confidence >= 0.35f;
}
