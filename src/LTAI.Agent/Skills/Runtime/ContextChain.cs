using LTAI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Skills.Runtime;

/// <summary>
/// A single round in a multi-round execution plan.
/// </summary>
public sealed record RoundPlan
{
    public int Index { get; init; }
    public string Goal { get; init; } = "";
    public string Context { get; init; } = "";
    public List<string> MatchedSkillIds { get; init; } = new();
    public string? Prompt { get; init; }
}

/// <summary>
/// Accumulated context across rounds. Controls token budget to prevent overflow.
/// </summary>
public sealed class ContextChain
{
    private readonly List<(string Role, string Content)> _history = new();
    private readonly int _maxTokens;
    private int _currentTokens;

    public IReadOnlyList<(string Role, string Content)> History => _history;
    public int TokenCount => _currentTokens;

    public ContextChain(int maxTokens = 8000)
    {
        _maxTokens = maxTokens;
    }

    public void AddRound(int roundIndex, string goal, string result)
    {
        var entry = $"## Round {roundIndex}: {goal}\n{Truncate(result, 1500)}";
        _history.Add(("system", entry));
        _currentTokens += EstimateTokens(entry);

        Compact();
    }

    public string BuildPrompt(string currentGoal, string? skillHints)
    {
        var sb = new System.Text.StringBuilder();

        if (_history.Count > 0)
        {
            sb.AppendLine("【已完成的前序步骤】");
            foreach (var (_, content) in _history.TakeLast(5))
            {
                var truncated = content.Length > 800 ? content[..800] + "..." : content;
                sb.AppendLine(truncated);
                sb.AppendLine("---");
            }
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(skillHints))
        {
            sb.AppendLine("【相关技能指南】");
            sb.AppendLine(skillHints);
            sb.AppendLine();
        }

        sb.AppendLine($"【当前步骤】{currentGoal}");
        sb.AppendLine("请仅完成此步骤，不要跳步。完成后给出明确结论。");

        return sb.ToString();
    }

    public string BuildSynthesisPrompt(string originalQuery, string domain)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"原始需求: {originalQuery}");
        sb.AppendLine($"领域: {domain}");
        sb.AppendLine();
        sb.AppendLine("以下是各步骤的执行结果：");

        foreach (var (_, content) in _history)
        {
            var truncated = content.Length > 600 ? content[..600] + "..." : content;
            sb.AppendLine(truncated);
            sb.AppendLine("---");
        }

        sb.AppendLine();
        sb.AppendLine("请综合以上所有步骤的结果，生成一份完整的最终回答。");

        return sb.ToString();
    }

    private void Compact()
    {
        while (_currentTokens > _maxTokens && _history.Count > 3)
        {
            var removed = _history[0];
            _history.RemoveAt(0);
            _currentTokens -= EstimateTokens(removed.Content);

            if (_history.Count > 0)
            {
                _history[0] = ("system", "(前序步骤已压缩) " + _history[0].Content);
            }
        }
    }

    private static int EstimateTokens(string text) => text.Length / 3;

    private static string Truncate(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..maxLen] + "...";
}
