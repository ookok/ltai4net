using System.Text.RegularExpressions;

namespace LTAI.Agent.Memory;

public sealed record MemoryRoute(
    int BasicBudget,
    int ReflectionBudget,
    int EntityBudget,
    double TemporalBoost,
    int MaxDrawers,
    IReadOnlyList<(string RoomPattern, double Boost)>? RoomBoosts = null)
{
    public static readonly MemoryRoute Default = new(
        BasicBudget: MemoryBudget.L4MaxTokens,
        ReflectionBudget: 400,
        EntityBudget: 300,
        TemporalBoost: 1.0,
        MaxDrawers: 5);
}

public sealed partial class QueryAwareMemoryRouter
{
    private static readonly Regex TemporalPattern = TemporalRegex();
    private static readonly Regex EntityPattern = EntityRegex();

    public MemoryRoute Route(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return MemoryRoute.Default;

        var temporal = HasTemporal(query);
        var entity = HasEntity(query);
        var complexity = EstimateComplexity(query);

        var basicBudget = MemoryBudget.L4MaxTokens;
        var reflectionBudget = 400;
        var entityBudget = 300;
        var temporalBoost = 1.0;
        var maxDrawers = 5;

        var boosts = new List<(string, double)>();

        if (temporal)
        {
            reflectionBudget += 300;
            temporalBoost = 1.3;
            boosts.Add(("reflection", 1.4));
        }

        if (entity)
        {
            entityBudget += 400;
            reflectionBudget += 200;
            boosts.Add((".entity", 1.5));
            boosts.Add(("memory", 1.2));
        }

        if (complexity <= 0.3f)
        {
            basicBudget = (int)(basicBudget * 0.5);
            reflectionBudget /= 2;
            entityBudget /= 2;
            maxDrawers = 3;
        }
        else if (complexity >= 0.7f)
        {
            basicBudget = (int)(basicBudget * 1.5);
            reflectionBudget = (int)(reflectionBudget * 1.3);
            maxDrawers = 8;
        }

        return new MemoryRoute(basicBudget, reflectionBudget, entityBudget, temporalBoost, maxDrawers, boosts);
    }

    private static bool HasTemporal(string query)
        => TemporalPattern.IsMatch(query);

    private static bool HasEntity(string query)
        => EntityPattern.IsMatch(query);

    private static float EstimateComplexity(string query)
    {
        var words = query.Split([' ', '\t', '\n', ',', '.', '?', '!'], StringSplitOptions.RemoveEmptyEntries);
        var wordCount = words.Length;

        var questionWords = 0;
        foreach (var w in words)
        {
            var lower = w.AsSpan();
            if (lower is "what" or "how" or "why" or "when" or "where" or "which" or "who"
                or "哪个" or "什么" or "怎么" or "为什么" or "何时" or "哪里")
                questionWords++;
        }

        var multiSentence = query.Contains('.') || query.Contains('？') || query.Contains('。');
        var hasConjunctions = query.Contains(" and ", StringComparison.OrdinalIgnoreCase)
                           || query.Contains(" or ", StringComparison.OrdinalIgnoreCase)
                           || query.Contains(" but ", StringComparison.OrdinalIgnoreCase)
                           || query.Contains("然后") || query.Contains("并且");

        float score = 0f;
        if (wordCount <= 3) score = 0.1f;
        else if (wordCount <= 8) score = 0.3f;
        else if (wordCount <= 15) score = 0.5f;
        else score = 0.7f;

        if (questionWords > 0) score += 0.1f;
        if (multiSentence) score += 0.1f;
        if (hasConjunctions) score += 0.1f;

        return Math.Clamp(score, 0f, 1f);
    }

    [GeneratedRegex(@"\b(yesterday|today|tomorrow|last\s+\w+|next\s+\w+|this\s+\w+|during|before|after|ago|from\s+\d+|between|\d{4}|\d{1,2}/\d{1,2}(?:/\d{2,4})?|昨天|今天|明天|上周|下周|这个月|去年|明年|最近|之前|之后)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, 500)]
    private static partial Regex TemporalRegex();

    [GeneratedRegex(@"\b([A-Z][a-z]+(?:\s+[A-Z][a-z]+)+)\b", RegexOptions.Compiled, 500)]
    private static partial Regex EntityRegex();
}
