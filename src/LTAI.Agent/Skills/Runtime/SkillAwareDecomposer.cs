using LTAI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Skills.Runtime;

/// <summary>
/// Decomposes long user queries into ordered multi-round plans,
/// guided by LLM reasoning and Skill library matching.
/// </summary>
public sealed class SkillAwareDecomposer
{
    private readonly SkillRegistry _registry;
    private readonly IChatClient _llm;
    private readonly ILogger<SkillAwareDecomposer> _logger;

    public SkillAwareDecomposer(SkillRegistry registry, IChatClient llm, ILogger<SkillAwareDecomposer>? logger = null)
    {
        _registry = registry;
        _llm = llm;
        _logger = logger ?? new NullLogger<SkillAwareDecomposer>();
    }

    /// <summary>
    /// Check if a query is long/complex enough to warrant multi-round decomposition.
    /// </summary>
    public bool NeedsDecomposition(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        if (query.Length < 80) return false;

        var sentences = query.Split(new[] { '.', '。', '!', '！', '?', '？', '\n', ';', '；' },
            StringSplitOptions.RemoveEmptyEntries);

        return sentences.Length >= 3 || query.Length > 300;
    }

    /// <summary>
    /// Decompose query into ordered rounds using LLM and Skill hints.
    /// </summary>
    public async Task<List<RoundPlan>> DecomposeAsync(string query, string domain, CancellationToken ct = default)
    {
        var relevantSkills = _registry.MatchByTrigger(query).Take(5).ToList();

        var skillHints = relevantSkills.Count > 0
            ? string.Join("\n", relevantSkills.Select(s =>
                $"  - {s.Name} ({s.LayerDir}): {s.Intent} (conf={s.Confidence:F2})"))
            : "";

        var prompt = $"""
            将以下复杂任务拆解为2-5个有序步骤，每个步骤应独立可执行。
            步骤之间应有依赖关系，前一步的输出是后一步的输入。

            {skillHints}

            任务: {query}
            领域: {domain}

            返回格式（每行一个步骤，以数字开头）:
            1. 步骤描述（应使用的工具或技能）
            2. 步骤描述
            ...
            """;

        try
        {
            var response = await _llm.GetResponseAsync(prompt, cancellationToken: ct).ConfigureAwait(false);
            var text = response.Text ?? "";
            return ParseRounds(text, relevantSkills);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM decomposition failed, using heuristic fallback");
            return HeuristicFallback(query);
        }
    }

    private List<RoundPlan> ParseRounds(string text, List<Skill> relevantSkills)
    {
        var rounds = new List<RoundPlan>();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 5) continue;
            if (!char.IsDigit(trimmed[0])) continue;

            var dotIdx = trimmed.IndexOfAny(new[] { '.', ')', '、', ' ' });
            if (dotIdx <= 0) continue;

            var goal = trimmed[(dotIdx + 1)..].Trim();

            var matchedSkills = relevantSkills
                .Where(s => s.Triggers.Any(t =>
                    goal.Contains(t.Pattern, StringComparison.OrdinalIgnoreCase)))
                .Select(s => s.Name)
                .ToList();

            rounds.Add(new RoundPlan
            {
                Index = rounds.Count + 1,
                Goal = goal,
                MatchedSkillIds = matchedSkills
            });
        }

        if (rounds.Count == 0)
            return HeuristicFallback(text);

        return rounds;
    }

    private static List<RoundPlan> HeuristicFallback(string query)
    {
        var sentences = query.Split(new[] { '.', '。', '!', '！', '?', '？', '\n', ';', '；' },
            StringSplitOptions.RemoveEmptyEntries);

        if (sentences.Length <= 1)
            return new List<RoundPlan> { new() { Index = 1, Goal = query } };

        return sentences
            .Select(s => s.Trim())
            .Where(s => s.Length > 10)
            .Select((s, i) => new RoundPlan { Index = i + 1, Goal = s })
            .Take(5)
            .ToList();
    }

    private sealed class NullLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
