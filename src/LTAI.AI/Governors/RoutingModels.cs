namespace LTAI.AI.Governors;

public enum MoEExpert
{
    Code, Math, Chat, Reasoning, EIA, General
}

public sealed record MoERouteResult
{
    public MoEExpert Primary { get; init; }
    public MoEExpert? Secondary { get; init; }
    public float PrimaryScore { get; init; }
    public float SecondaryScore { get; init; }
    public HrmReasoningTier RecommendedTier { get; init; }
    public string Reason { get; init; } = "";
}

public sealed record L2TeachingResult
{
    public string Answer { get; init; } = "";
    public string ReasoningSteps { get; init; } = "";
    public string KeyConcepts { get; init; } = "";
    public string SimplifiedExplanation { get; init; } = "";
    public List<string> FollowUpSuggestions { get; init; } = new();
}
