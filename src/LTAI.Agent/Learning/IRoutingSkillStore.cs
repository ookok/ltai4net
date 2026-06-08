// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  IRoutingSkillStore — routing skill memory interface
//
//  Inspiration: MUSE-Autoskill (arXiv 2605.27366)
//  "Agent skills as long-lived, experience-aware assets"
//
//  Tracks per-specialist success/failure history and adjusts
//  routing confidence thresholds over time.
//
//  LTAI adaptation: lightweight routing-level skill store
//  that works alongside SkillEvolutionEngine (which handles
//  tool-level skills). This store focuses on:
//    - Which specialist agents succeed for which query types
//    - Confidence threshold tuning per specialist
//    - Greeting fast-path effectiveness tracking
// ═══════════════════════════════════════════════════════════════

namespace LTAI.Agent.Learning;

/// <summary>
/// Stores and retrieves routing skill data: per-specialist success
/// history, query-type patterns, and optimized confidence thresholds.
/// </summary>
public interface IRoutingSkillStore
{
    /// <summary>
    /// Record the outcome of a routing decision.
    /// </summary>
    /// <param name="query">User query.</param>
    /// <param name="specialist">Chosen specialist agent name.</param>
    /// <param name="success">Did the specialist handle it well?</param>
    /// <param name="latencyMs">Execution latency.</param>
    /// <param name="tokensUsed">Approximate tokens used.</param>
    void RecordOutcome(string query, string specialist, bool success, long latencyMs, int tokensUsed);

    /// <summary>
    /// Get success rate for a specialist (0-1).
    /// Returns 0.5 (neutral) if no data.
    /// </summary>
    double GetSuccessRate(string specialist);

    /// <summary>
    /// Get recommended confidence boost for a specialist.
    /// Positive = more likely to be selected; negative = less likely.
    /// Range: [-0.3, +0.3].
    /// </summary>
    double GetConfidenceBoost(string specialist);

    /// <summary>
    /// Get specialists that perform best for a given query type.
    /// </summary>
    IReadOnlyList<(string specialist, double score)> GetTopForQueryType(string queryType, int topK = 3);

    /// <summary>
    /// Detect query type from a query string (simple heuristic).
    /// </summary>
    string DetectQueryType(string query);

    /// <summary>
    /// Get all recorded stats for analysis/display.
    /// </summary>
    IReadOnlyDictionary<string, RoutingSkillStat> GetAllStats();

    /// <summary>
    /// Clear all records.
    /// </summary>
    void Reset();
}

/// <summary>
/// Per-specialist routing statistics.
/// </summary>
public sealed class RoutingSkillStat
{
    public string Specialist { get; init; } = "";
    public int TotalCalls;
    public int Successes;
    public long TotalLatencyMs;
    public int TotalTokens;
    public DateTime LastUsed = DateTime.MinValue;
    public readonly Dictionary<string, int> QueryTypeCounts = new(StringComparer.OrdinalIgnoreCase);

    public double SuccessRate => TotalCalls > 0 ? (double)Successes / TotalCalls : 0.5;
    public double AvgLatencyMs => TotalCalls > 0 ? (double)TotalLatencyMs / TotalCalls : 0;
    public int TotalFailures => TotalCalls - Successes;
    public string DominantQueryType => QueryTypeCounts.OrderByDescending(kv => kv.Value).FirstOrDefault().Key ?? "unknown";
}
