namespace LTAI.Agent.Experts.Routing;

/// <summary>
/// Merged output from multiple expert queries, ready for injection
/// into the LLM generator context.
/// </summary>
public sealed record AggregatedContext(
    string Content,
    IReadOnlyList<Citation> AllCitations,
    float AggregateConfidence,
    bool HasAnswer
);
