using System.Text.RegularExpressions;
using LTAI.Core.Configuration;
using LTAI.Agent.Memory;

namespace LTAI.Agent;

public class DefaultEscalationDecider : IEscalationDecider
{
    private readonly double _calibratedScoreThreshold;
    private readonly double _valueOfInfoThreshold;
    private readonly double _shouldEscalateGapThreshold;
    private readonly int _shouldEscalateSupportThreshold;
    private readonly int _shouldEscalateStepsThreshold;

    public DefaultEscalationDecider(EscalationConfig? config = null)
    {
        var cfg = config ?? new EscalationConfig();
        _calibratedScoreThreshold = cfg.CalibratedScoreThreshold;
        _valueOfInfoThreshold = cfg.ValueOfInfoThreshold;
        _shouldEscalateGapThreshold = cfg.ShouldEscalateGapThreshold;
        _shouldEscalateSupportThreshold = cfg.ShouldEscalateSupportThreshold;
        _shouldEscalateStepsThreshold = cfg.ShouldEscalateStepsThreshold;
    }
    private static readonly HashSet<string> ToolRequiredKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "search", "查找", "find", "lookup", "查询", "计算",
        "compile", "build", "run", "执行", "运行", "编译",
        "git", "commit", "push", "pull", "branch",
        "file", "文件", "read", "写", "write",
        "code", "代码", "analyze", "分析",
        "翻译", "translate", "summarize", "总结",
        "draw", "画", "diagram", "图",
        "create", "创建", "delete", "删除", "update", "更新"
    };

    private static readonly string[] CantPatterns =
    [
        "无法获取", "无法确定", "无法提供", "没有权限", "无法访问",
        "无法直接", "无法知道", "不知道当前", "不知道今天",
        "我不知道", "我不确定", "没有内置", "没有实时",
        "抱歉", "我无法", "暂时无法", "目前还不支持", "请稍后再试",
        "我不能", "我不可以", "对不起", "不支持", "暂不支持",
        "cannot", "can't", "unable to", "don't have", "don't know",
        "no access", "not have access", "not able to", "i don't"
    ];

    // Task type keywords for classification
    private static readonly Dictionary<string, string[]> TaskTypeKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["code"] = ["代码", "code", "函数", "function", "类", "class", "方法", "method", "实现", "implement", "修复", "fix", "bug", "debug"],
        ["file"] = ["文件", "file", "读取", "read", "写入", "write", "编辑", "edit", "删除", "delete", "创建", "create"],
        ["search"] = ["搜索", "search", "查找", "find", "查找", "lookup", "查询", "query"],
        ["data"] = ["数据", "data", "csv", "json", "excel", "表格", "table", "数据库", "database", "sql"],
        ["web"] = ["网页", "web", "网站", "website", "url", "链接", "link", "http", "api"],
        ["image"] = ["图片", "image", "图像", "picture", "照片", "photo", "png", "jpg"],
        ["git"] = ["git", "commit", "push", "pull", "branch", "merge", "diff", "log"],
        ["text"] = ["写", "write", "文档", "document", "文章", "article", "翻译", "translate", "总结", "summarize"],
    };

    // Multi-step patterns
    private static readonly string[] MultiStepPatterns =
    [
        "然后", "接着", "之后", "最后", "首先", "第一步", "第二步",
        "first", "then", "next", "finally", "step 1", "step 2",
        "先.*再", "before.*after"
    ];

    /// <summary>
    /// 判断是否为简单查询（无需路由/升级）。
    /// 改进：增加混合查询检测、模式匹配、工具依赖检测。
    /// </summary>
    public bool IsSimpleQuery(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var trimmed = message.Trim();

        // Fast path: delegate to unified QueryClassifier
        if (QueryClassifier.IsGreetingOnlyStatic(trimmed))
            return true;

        // Fast path: very short messages without tool keywords
        if (trimmed.Length <= 6 && !ToolRequiredKeywords.Any(k => trimmed.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Mixed query detection: greeting-like prefix + tool keyword = NOT simple
        if (trimmed.Length > 20)
        {
            var hasGreeting = QueryClassifier.IsGreetingOnlyStatic(trimmed[..Math.Min(20, trimmed.Length)]);
            var hasToolKeyword = ToolRequiredKeywords.Any(k => trimmed.Contains(k, StringComparison.OrdinalIgnoreCase));
            if (hasGreeting && hasToolKeyword)
                return false;
        }

        // Pattern-based: questions with specific intent are NOT simple
        if (trimmed.Contains('?') || trimmed.Contains('？'))
        {
            var questionWords = new[] { "怎么", "如何", "为什么", "什么", "哪", "多少", "how", "why", "what", "where", "when" };
            if (questionWords.Any(w => trimmed.Contains(w, StringComparison.OrdinalIgnoreCase)))
                return false; // Specific question, needs LLM
        }

        return false;
    }

    /// <summary>
    /// 估算任务复杂度（0-7）。
    /// 改进：增加任务类型分类、多步检测、工具依赖检测。
    /// </summary>
    public int EstimateComplexity(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return 0;
        var parts = message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var score = parts.Length switch
        {
            <= 5 => 1,
            <= 15 => 2,
            <= 30 => 3,
            <= 50 => 4,
            _ => 5
        };

        // Tool dependency detection
        var toolKeywordCount = ToolRequiredKeywords.Count(k => message.Contains(k, StringComparison.OrdinalIgnoreCase));
        if (toolKeywordCount >= 2) score += 2;
        else if (toolKeywordCount == 1) score += 1;

        // Multi-step detection
        if (MultiStepPatterns.Any(p => Regex.IsMatch(message, p, RegexOptions.IgnoreCase)))
            score += 1;

        // Code complexity indicators
        if (message.Contains("```") || message.Contains("class ") || message.Contains("function "))
            score += 1;

        // Length-based adjustment
        if (message.Length > 200) score += 1;
        if (message.Contains('\n')) score += 1;

        return Math.Clamp(score, 0, 7);
    }

    /// <summary>
    /// 分类任务类型。
    /// </summary>
    public string ClassifyTaskType(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "general";

        var scores = new Dictionary<string, int>();
        foreach (var (type, keywords) in TaskTypeKeywords)
        {
            scores[type] = keywords.Count(k => message.Contains(k, StringComparison.OrdinalIgnoreCase));
        }

        var best = scores.OrderByDescending(kv => kv.Value).First();
        return best.Value > 0 ? best.Key : "general";
    }

    /// <summary>
    /// 检测是否为多步任务。
    /// </summary>
    public bool IsMultiStep(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        return MultiStepPatterns.Any(p => Regex.IsMatch(message, p, RegexOptions.IgnoreCase));
    }

    public (bool needsPro, string reason, double confidence) Evaluate(
        string message, string response, L1State l1State,
        double entropy, double valueOfInfo,
        bool steerJudgeSaysInadequate, string? steerJudgeReason)
    {
        var calibratedScore = entropy * 0.4 + l1State.Gap * 0.4 - l1State.SupportCount * 0.05;
        calibratedScore = Math.Clamp(calibratedScore, 0.0, 1.0);

        // Preserve L1State.ShouldEscalate logic but with configurable thresholds
        var shouldEscalateByState = l1State.Label == "escalate" ||
            (l1State.Candidates.Count >= 2 && l1State.Gap < _shouldEscalateGapThreshold && l1State.SupportCount < _shouldEscalateSupportThreshold) ||
            l1State.StepsTaken >= _shouldEscalateStepsThreshold;

        var needsPro = shouldEscalateByState || calibratedScore > _calibratedScoreThreshold || valueOfInfo > _valueOfInfoThreshold;
        var reason = l1State.EscalationReason ?? "complex task";

        if (steerJudgeSaysInadequate)
        {
            needsPro = true;
            reason = steerJudgeReason ?? "judge deemed inadequate";
        }

        if (!needsPro)
        {
            var signal = EscalationSignal.FromString(response);
            if (signal != null)
            {
                needsPro = true;
                reason = signal.Reason;
            }
        }

        return (needsPro, reason, calibratedScore);
    }

    public bool ContainsRefusalPatterns(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lower = text.ToLowerInvariant();
        return CantPatterns.Any(p => lower.Contains(p, StringComparison.OrdinalIgnoreCase))
            || text.Contains("{{") || text.Contains("TODO");
    }
}
