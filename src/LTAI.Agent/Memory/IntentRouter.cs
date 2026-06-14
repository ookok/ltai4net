using LTAI.AI;

namespace LTAI.Agent.Memory;

public enum QueryIntent
{
    What,
    When,
    Why,
    Where,
    Who,
    How,
}

public sealed class IntentRouter
{
    private const int EmbeddingDim = 384;
    private const float ConfidentThreshold = 0.35f;
    private const float FallbackThreshold = 0.15f;

    private static readonly (QueryIntent Intent, string[] Anchors)[] IntentAnchors =
    [
        (QueryIntent.Why, ["为什么", "为何", "原因", "怎么造成", "起因", "根源", "根据什么",
                            "why", "reason", "cause", "explanation", "what caused"]),
        (QueryIntent.When, ["什么时候", "何时", "时间", "几点", "日期", "哪一年", "哪一天",
                            "when", "timeline", "time", "date", "how long ago"]),
        (QueryIntent.Who, ["谁", "哪个", "哪一个", "什么人", "作者", "负责人",
                           "who", "which", "whose", "author", "responsible"]),
        (QueryIntent.Where, ["哪里", "哪儿", "什么地方", "位置", "在哪", "何处",
                             "where", "location", "place", "path", "directory"]),
        (QueryIntent.How, ["怎么", "如何", "怎样", "怎么做", "步骤", "方法", "流程",
                           "how", "how to", "step", "method", "approach", "procedure"]),
        (QueryIntent.What, ["什么", "是什么", "定义", "解释", "含义", "概念",
                            "what", "define", "definition", "meaning", "describe", "explain"]),
    ];

    private static readonly Lazy<Dictionary<QueryIntent, float[]>> _centroids = new(() =>
    {
        return IntentAnchors.ToDictionary(
            a => a.Intent,
            a => EmbeddingClient.FastEmb(string.Join(" ", a.Anchors), EmbeddingDim));
    }, true);

    private static readonly (string Pattern, QueryIntent Intent)[] KeywordFallback =
    [
        ("为什么", QueryIntent.Why), ("为何", QueryIntent.Why), ("原因", QueryIntent.Why),
        ("怎么造成", QueryIntent.Why), ("why", QueryIntent.Why), ("reason", QueryIntent.Why),
        ("cause", QueryIntent.Why),
        ("什么时候", QueryIntent.When), ("何时", QueryIntent.When), ("时间", QueryIntent.When),
        ("when", QueryIntent.When), ("timeline", QueryIntent.When),
        ("谁", QueryIntent.Who), ("哪个", QueryIntent.Who), ("who", QueryIntent.Who),
        ("which", QueryIntent.Who), ("whose", QueryIntent.Who),
        ("哪里", QueryIntent.Where), ("哪儿", QueryIntent.Where), ("where", QueryIntent.Where),
        ("location", QueryIntent.Where),
        ("怎么", QueryIntent.How), ("如何", QueryIntent.How), ("怎样", QueryIntent.How),
        ("how", QueryIntent.How),
    ];

    public QueryIntent Classify(string query)
    {
        return ClassifyWithScore(query).Intent;
    }

    public (QueryIntent Intent, float Confidence) ClassifyWithScore(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return (QueryIntent.What, 1.0f);

        var queryEmb = EmbeddingClient.FastEmb(query.Trim(), EmbeddingDim);
        var centroids = _centroids.Value;

        QueryIntent bestIntent = QueryIntent.What;
        float bestScore = -1f;
        float secondBest = -1f;

        foreach (var (intent, centroid) in centroids)
        {
            var score = VectorMath.CosineSimilarity(queryEmb.AsSpan(), centroid.AsSpan());
            if (score > bestScore)
            {
                secondBest = bestScore;
                bestScore = score;
                bestIntent = intent;
            }
            else if (score > secondBest)
            {
                secondBest = score;
            }
        }

        var margin = bestScore - secondBest;

        if (bestScore >= ConfidentThreshold || margin >= 0.08f)
            return (bestIntent, bestScore);

        if (bestScore >= FallbackThreshold)
            return (bestIntent, bestScore * 0.7f);

        var lowered = query.AsSpan();
        foreach (var (pattern, intent) in KeywordFallback)
        {
            if (lowered.IndexOf(pattern.AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0)
                return (intent, 0.3f);
        }

        return (QueryIntent.What, 0.1f);
    }
}
