using LTAI.Agent.Tools.Review;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Pipeline.Steps;

public enum QualityLevel { Excellent = 5, Good = 4, Acceptable = 3, Poor = 2, Unacceptable = 1 }

public sealed record QualityGateResult
{
    public QualityLevel Level { get; init; } = QualityLevel.Acceptable;
    public double Score { get; init; }
    public List<string> Issues { get; init; } = [];
    public bool Passed => Score >= PassThreshold;
    public int RetryCount { get; init; }
    public double PassThreshold { get; init; } = 0.6;
}

public sealed class QualityGateStep : IPipelineStep
{
    private readonly ILogger<QualityGateStep> _logger;
    private readonly ReviewRuleEngine? _ruleEngine;
    private readonly int _maxRetries;

    public string Name => "QualityGate";

    public QualityGateStep(ILogger<QualityGateStep>? logger = null,
        ReviewRuleEngine? ruleEngine = null, int maxRetries = 2)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<QualityGateStep>.Instance;
        _ruleEngine = ruleEngine;
        _maxRetries = maxRetries;
    }

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        var lastMsg = context.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
        if (lastMsg == null || string.IsNullOrWhiteSpace(lastMsg.Text))
            return context;

        var result = await EvaluateQualityAsync(lastMsg.Text, context).ConfigureAwait(false);
        context.Set("QualityGateResult", result);

        if (!result.Passed)
        {
            context.Set("QualityGateBlocked", true);
            var msg = $"⚠️ 质量门禁未通过 (得分 {result.Score:P1})\n问题:\n"
                      + string.Join("\n", result.Issues.Select(i => $"- {i}"));
            context.Messages.Add(new ChatMessage(ChatRole.System, msg));
            _logger.LogWarning("QualityGate: blocked (score={Score:P1}, issues={Count})",
                result.Score, result.Issues.Count);
        }
        else
        {
            context.Set("QualityGateBlocked", false);
            _logger.LogDebug("QualityGate: passed (score={Score:P1})", result.Score);
        }

        return context;
    }

    private Task<QualityGateResult> EvaluateQualityAsync(string text, MessageContext context)
    {
        var issues = new List<string>();
        var score = 1.0;

        if (text.Length < 20) { issues.Add("回复过短"); score -= 0.3; }
        if (text.Length > 8000) { issues.Add("回复过长"); score -= 0.1; }
        if (!text.Contains('。') && !text.Contains('\n') && text.Length > 200)
        { issues.Add("缺少分段和句号"); score -= 0.15; }
        if (text.Contains("我不确定", StringComparison.Ordinal) ||
            text.Contains("我无法", StringComparison.Ordinal) ||
            text.Contains("我不清楚", StringComparison.Ordinal))
        { issues.Add("包含不确定性表述"); score -= 0.2; }
        if (context.TryGet<bool>("GrammarCheckBlocked", out var blocked) && blocked)
        { issues.Add("存在语法错误"); score -= 0.3; }
        if (text.Trim().StartsWith("抱歉", StringComparison.Ordinal) ||
            text.Trim().StartsWith("对不起", StringComparison.Ordinal))
        { issues.Add("以道歉开头"); score -= 0.1; }

        var toolCallCount = context.ToolCalls.Count;
        if (toolCallCount == 0 && text.Length > 100)
        { issues.Add("未调用工具但输出了长回复"); score -= 0.1; }

        score = Math.Clamp(score, 0.0, 1.0);
        var level = score switch
        {
            >= 0.9 => QualityLevel.Excellent,
            >= 0.75 => QualityLevel.Good,
            >= 0.6 => QualityLevel.Acceptable,
            >= 0.4 => QualityLevel.Poor,
            _ => QualityLevel.Unacceptable
        };

        return Task.FromResult(new QualityGateResult
        {
            Level = level,
            Score = score,
            Issues = issues
        });
    }
}
