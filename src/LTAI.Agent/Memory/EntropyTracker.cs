using System.Collections.Concurrent;
using LTAI.Agent.Experts.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Memory;

/// <summary>
/// Entropy-driven retrieval boost tracker. Monitors per-expert confidence
/// from <see cref="ExpertFeedbackLogger"/> and computes uncertainty weights
/// for memory retrieval.
///
/// Principle: domains where the system has low confidence (high uncertainty)
/// get a retrieval boost — the system actively seeks information where it's
/// most confused, rather than where it's most comfortable.
///
/// Inspired by Fuzzy-Trace Theory's entropy-driven top-down retrieval:
/// uncertain gist representations trigger deeper searches.
/// </summary>
public sealed class EntropyTracker
{
    private readonly ExpertFeedbackLogger? _feedback;
    private readonly ILogger<EntropyTracker>? _logger;

    public EntropyTracker(ExpertFeedbackLogger? feedback = null, ILogger<EntropyTracker>? logger = null)
    {
        _feedback = feedback;
        _logger = logger;
    }

    /// <summary>
    /// Get uncertainty boost for a given domain key.
    /// Range: [-0.15, +0.25]. Positive = high uncertainty, boost retrieval.
    /// Negative = confident, slightly deprioritize (reduce noise).
    /// </summary>
    public double GetUncertaintyBoost(string domainKey)
    {
        if (_feedback == null) return 0;

        var stats = _feedback.GetStats();
        if (!stats.TryGetValue(domainKey, out var stat) || stat.SelectionCount < 3)
            return 0.05;

        var uncertainty = 1.0 - stat.SuccessRate;
        var boost = (uncertainty - 0.5) * 0.5;
        return Math.Clamp(boost, -0.15, 0.25);
    }

    /// <summary>
    /// Modality-aware confidence threshold for a PalaceStore wing (room).
    /// Each modality has a different natural similarity floor:
    ///   Code symbols: 0.35   (high precision, exact match)
    ///   Knowledge:    0.25   (entity/relation, medium)
    ///   Documents:    0.18   (fuzzy semantic)
    ///   Tools:        0.30   (structured schema)
    ///   Skills:       0.22   (text matching)
    ///
    /// The base threshold is lowered by the uncertainty boost for the wing.
    /// </summary>
    public float GetRoomThreshold(string? wing)
    {
        var baseThreshold = wing switch
        {
            "code" => 0.35f,
            "knowledge" => 0.25f,
            "docs" => 0.18f,
            "tools" => 0.30f,
            "diary" => 0.15f,
            _ => 0.25f
        };
        var boost = GetWingBoost(wing);
        return (float)(baseThreshold - Math.Max(0, boost));
    }

    /// <summary>
    /// Get boost for a wing (memory category). Maps wings to expert domains
    /// and returns the average uncertainty boost.
    /// </summary>
    public double GetWingBoost(string? wing)
    {
        if (string.IsNullOrEmpty(wing)) return 0;
        var key = wing switch
        {
            "code" => "codegraph/sharded",
            "knowledge" => "kg/expert",
            "docs" => "doc/api-expert",
            "tools" => "tool/expert",
            _ => wing
        };
        return GetUncertaintyBoost(key);
    }
}
