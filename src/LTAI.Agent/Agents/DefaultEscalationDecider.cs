using System.Text.RegularExpressions;

namespace LTAI.Agent;

public sealed partial class DefaultEscalationDecider : IEscalationDecider
{
    private static readonly HashSet<string> SimpleQueries = new(StringComparer.OrdinalIgnoreCase)
    {
        "hello", "hi", "hey", "你好", "嗨", "早上好", "下午好", "晚上好",
        "good morning", "good afternoon", "good evening",
        "who are you", "你是谁", "help", "帮助", "/help",
        "status", "状态", "/status", "thanks", "谢谢", "thank you"
    };

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

    public bool IsSimpleQuery(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var trimmed = message.Trim();
        return trimmed.Length <= 10 || SimpleQueries.Contains(trimmed);
    }

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
        if (ToolRequiredKeywords.Any(k => message.Contains(k, StringComparison.OrdinalIgnoreCase)))
            score += 1;
        if (message.Contains('\n')) score += 1;
        if (message.Length > 200) score += 1;
        return Math.Min(score, 7);
    }

    public (bool needsPro, string reason, double confidence) Evaluate(
        string message, string response, L1State l1State,
        double entropy, double valueOfInfo,
        bool steerJudgeSaysInadequate, string? steerJudgeReason)
    {
        var calibratedScore = entropy * 0.4 + l1State.Gap * 0.4 - l1State.SupportCount * 0.05;
        calibratedScore = Math.Clamp(calibratedScore, 0.0, 1.0);

        var needsPro = l1State.ShouldEscalate || calibratedScore > 0.6 || valueOfInfo > 0.5;
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
