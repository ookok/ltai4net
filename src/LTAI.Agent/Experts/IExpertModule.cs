namespace LTAI.Agent.Experts;

/// <summary>
/// Standardised expert contract for MoE-style sparse activation.
/// Every knowledge domain (KG, code graph, documents, tools, skills)
/// implements this interface so the Router can treat them uniformly.
/// </summary>
public interface IExpertModule
{
    string ExpertId { get; }
    ExpertDomain Domain { get; }
    string CapabilityDescription { get; }
    IReadOnlyList<string> KnowledgeTags { get; }

    /// <summary>
    /// Minimum confidence threshold for this expert's retrieval results.
    /// Different modalities have different natural similarity distributions:
    ///   KG/Code (exact match): 0.3-0.5
    ///   Documents (semantic):  0.15-0.25
    ///   Tools (structured):    0.3-0.4
    /// </summary>
    float MinConfidence { get; }

    Task<ExpertResponse> QueryAsync(ExpertQuery query, CancellationToken ct = default);
}
