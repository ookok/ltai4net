namespace LTAI.Agent.Experts.Routing;

/// <summary>
/// Output of the ExpertRouter: which experts to activate and why.
/// </summary>
public sealed record ExpertSelectionResult(
    IReadOnlyList<ExpertSelection> Selections,
    string? Reasoning
);

/// <summary>
/// A single expert selected by the Router for querying.
/// </summary>
public sealed record ExpertSelection(
    string ExpertId,
    float Confidence,
    string Rationale
);
