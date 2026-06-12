namespace LTAI.Agent.Experts;

/// <summary>
/// Structured query dispatched to an <see cref="IExpertModule"/>.
/// Populated by the Router with intent, entity links, and topic tags
/// before fan-out to selected experts.
/// </summary>
public sealed record ExpertQuery(
    string Query,
    string? Intent = null,
    IReadOnlyList<string>? EntityLinks = null,
    IReadOnlyList<string>? TopicTags = null,
    int MaxResults = 5
);
