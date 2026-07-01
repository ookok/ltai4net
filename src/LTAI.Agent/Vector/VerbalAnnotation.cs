// Copyright (c) LTAI. All rights reserved.

using System.Text.Json.Serialization;

namespace LTAI.Agent.Vector;

/// <summary>
/// Verbal-R3 inspired verbal annotation: analytic narrative explaining the logical
/// connection between a search query and a retrieved item.
/// Reference: arXiv:2605.01399 (ACL 2026)
/// </summary>
public sealed record VerbalAnnotation
{
    /// <summary>Relevance score (0.0–1.0).</summary>
    [JsonPropertyName("score")]
    public float Score { get; init; }

    /// <summary>
    /// Verbal rationale explaining why this item is (or isn't) relevant.
    /// An analytic narrative that articulates the logical connection between
    /// the search query and the retrieved context.
    /// </summary>
    [JsonPropertyName("rationale")]
    public string Rationale { get; init; } = "";

    /// <summary>Confidence level of this annotation.</summary>
    [JsonPropertyName("confidence")]
    public AnnotationConfidence Confidence { get; init; } = AnnotationConfidence.Medium;

    /// <summary>
    /// Optional suggestion: what the Generator should do with this item
    /// (e.g., "引用该段作为主要证据", "需进一步验证再使用").
    /// </summary>
    [JsonPropertyName("suggestion")]
    public string? Suggestion { get; init; }

    /// <summary>The retrieved item's source identifier (file path, node ID, etc.).</summary>
    [JsonPropertyName("source_id")]
    public string? SourceId { get; init; }
}

/// <summary>Confidence level for a verbal annotation.</summary>
public enum AnnotationConfidence
{
    Low = 0,
    Medium = 1,
    High = 2
}

/// <summary>
/// Collection of verbal annotations for a single retrieval round.
/// Used to flow Verbal-R3 annotations through the pipeline via MessageContext.
/// </summary>
public sealed record VerbalAnnotationSet
{
    /// <summary>Query that produced these annotations.</summary>
    public string Query { get; init; } = "";

    /// <summary>Per-item verbal annotations.</summary>
    public List<VerbalAnnotation> Annotations { get; init; } = [];

    /// <summary>
    /// Average confidence across all annotations.
    /// Used by relevance-guided test-time scaling to decide whether to expand search.
    /// </summary>
    public float AverageConfidence => Annotations.Count > 0
        ? (float)(Annotations.Average(a => (int)a.Confidence) / 2.0)
        : 0f;

    /// <summary>
    /// Proportion of annotations with High confidence.
    /// </summary>
    public float HighConfidenceRatio => Annotations.Count > 0
        ? Annotations.Count(a => a.Confidence == AnnotationConfidence.High) / (float)Annotations.Count
        : 0f;
}
