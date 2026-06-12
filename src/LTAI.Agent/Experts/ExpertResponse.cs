namespace LTAI.Agent.Experts;

/// <summary>
/// Normalized response from a single expert module.
/// Every expert must return this contract so the Aggregator can
/// merge, deduplicate, and resolve conflicts without knowing
/// internal implementations.
/// </summary>
public sealed record ExpertResponse(
    string ExpertId,
    string Content,
    float Confidence,
    IReadOnlyList<Citation> Citations,
    ProvenanceInfo Provenance,
    bool NoAnswer = false,
    string? ClarifyQuestion = null
);

/// <summary>
/// A reference to a source that supports a claim in expert output.
/// </summary>
public sealed record Citation(
    string Id,
    string Title,
    string Source,
    CitationType Type,
    float Relevance = 1.0f
);

/// <summary>
/// What kind of source this citation points to.
/// </summary>
public enum CitationType
{
    Doc,
    Code,
    Fact,
    ToolResult,
    Skill,
    Unknown
}

/// <summary>
/// Where the expert's knowledge came from and when it was last updated.
/// </summary>
public sealed record ProvenanceInfo(
    string SourceGraph,
    DateTime? LastUpdated,
    string? StalenessNote = null
);
