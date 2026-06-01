// Copyright (c) LTAI. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LTAI.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Workflows;

/// <summary>
/// P7.7 decision-tree router — replaces the flat top-K selection with a 3-stage
/// confidence cascade that gives the MAF <c>HandoffWorkflowBuilder</c> either a
/// tight top-K candidate set (when embedding is confident) or all specialists
/// (when embedding is ambiguous).
/// </summary>
/// <remarks>
/// <para><b>Stages:</b></para>
/// <list type="number">
/// <item><description><b>Embedding top-K</b>: cosine similarity ranks all agents, take K (default 3).</description></item>
/// <item><description><b>Confidence margin</b>: <c>margin = score[0] - score[1]</c>. A high margin means
///   rank-1 is meaningfully better than rank-2 — the embedding has converged on one agent.</description></item>
/// <item><description><b>Branch:</b>
///   <list type="bullet">
///     <item><description><b>confident</b> (margin ≥ <see cref="Options.ConfidenceMarginThreshold"/>, default 0.15):
///       narrow to top-K → fast routing, low LLM cost.</description></item>
///     <item><description><b>ambiguous</b> (margin &lt; threshold OR top-1 score &lt;
///       <see cref="Options.MinTopScoreThreshold"/>, default 0.30): expand to all specialists →
///       slower but higher recall.</description></item>
///   </list>
///   </description></item>
/// </list>
/// <para>
/// The decision is logged via <see cref="ILogger"/> so the OTel exporter (P7.2) can surface
/// routing hit-rate in the dashboard. Thresholds are tunable via
/// <see cref="DecisionTreeRouterOptions"/>.
/// </para>
/// </remarks>
public sealed class DecisionTreeRouter
{
    private readonly EmbeddingClient? _embedder;
    private readonly ILogger<DecisionTreeRouter> _logger;
    private readonly DecisionTreeRouterOptions _options;

    public DecisionTreeRouter(
        EmbeddingClient? embedder,
        ILogger<DecisionTreeRouter> logger,
        DecisionTreeRouterOptions? options = null)
    {
        _embedder = embedder;
        _logger = logger;
        _options = options ?? new DecisionTreeRouterOptions();
    }

    /// <summary>
    /// Run the decision tree. Returns the chosen candidate names plus the
    /// diagnostics describing the branch taken — both for the caller's use
    /// and for telemetry.
    /// </summary>
    public async Task<DecisionTreeResult> RouteAsync(
        string task,
        IReadOnlyCollection<string> allSpecialistNames,
        CancellationToken ct = default)
    {
        var allNames = allSpecialistNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        // Stage 0: no embedder → use all (legacy behavior)
        if (_embedder is null)
        {
            _logger.LogDebug("Router: no embedder, using all {N} specialists", allNames.Length);
            return new DecisionTreeResult(allNames, BranchKind.NoEmbedder, 0f, 0f, []);
        }

        // Stage 1: top-K by cosine similarity
        var topK = await AgentRegistry
            .SelectTopKWithScoresAsync(task, _embedder, k: _options.TopK, ct)
            .ConfigureAwait(false);

        if (topK.Count == 0)
        {
            _logger.LogWarning("Router: top-K returned empty, using all {N} specialists", allNames.Length);
            return new DecisionTreeResult(allNames, BranchKind.EmbeddingFailed, 0f, 0f, []);
        }

        // Stage 2: confidence margin
        var topScore = topK[0].Score;
        var margin = topK.Count >= 2 ? topScore - topK[1].Score : float.MaxValue;

        // Stage 3: branch
        var confident = margin >= _options.ConfidenceMarginThreshold
                        && topScore >= _options.MinTopScoreThreshold;

        if (confident)
        {
            var chosen = topK
                .Select(t => t.Name)
                .Where(n => allNames.Contains(n, StringComparer.OrdinalIgnoreCase))
                .ToArray();

            // Defensive: if filtering dropped everything (e.g. names changed), fall back to all
            if (chosen.Length == 0) chosen = allNames;

            _logger.LogInformation(
                "Router: CONFIDENT (margin={Margin:F3} ≥ {Threshold:F3}, top={Top:F3}) → top-{K} of {Total}",
                margin, _options.ConfidenceMarginThreshold, topScore, chosen.Length, allNames.Length);
            return new DecisionTreeResult(chosen, BranchKind.ConfidentTopK, topScore, margin, topK);
        }
        else
        {
            var reason = margin < _options.ConfidenceMarginThreshold
                ? $"margin={margin:F3} < {_options.ConfidenceMarginThreshold:F3}"
                : $"top={topScore:F3} < {_options.MinTopScoreThreshold:F3}";

            _logger.LogInformation(
                "Router: AMBIGUOUS ({Reason}) → falling back to all {N} specialists",
                reason, allNames.Length);
            return new DecisionTreeResult(allNames, BranchKind.AmbiguousFallback, topScore, margin, topK);
        }
    }
}

/// <summary>
/// Tunable thresholds for <see cref="DecisionTreeRouter"/>. Defaults are
/// chosen for an 18-agent pool with English+Chinese capability descriptions.
/// </summary>
public sealed class DecisionTreeRouterOptions
{
    /// <summary>Number of candidates to take from the embedding top-K (Stage 1).</summary>
    public int TopK { get; init; } = 3;

    /// <summary>
    /// Minimum margin (top-1 score − top-2 score) to consider the embedding
    /// confident. Below this, fall back to all specialists. Default 0.15.
    /// </summary>
    public float ConfidenceMarginThreshold { get; init; } = 0.15f;

    /// <summary>
    /// Minimum absolute top-1 score to consider the embedding confident.
    /// Below this even with a high margin, the embedding has found nothing
    /// relevant — fall back. Default 0.30.
    /// </summary>
    public float MinTopScoreThreshold { get; init; } = 0.30f;
}

/// <summary>Why the router took the branch it did. Useful for telemetry.</summary>
public enum BranchKind
{
    /// <summary>No embedder was registered; used all specialists (legacy fallback).</summary>
    NoEmbedder,
    /// <summary>Embedding top-K retrieval failed; used all specialists.</summary>
    EmbeddingFailed,
    /// <summary>Margin was high; routed to top-K candidates.</summary>
    ConfidentTopK,
    /// <summary>Margin was low; fell back to all specialists.</summary>
    AmbiguousFallback,
}

/// <summary>Result of <see cref="DecisionTreeRouter.RouteAsync"/>.</summary>
/// <param name="Candidates">Final list of agent names to pass to the handoff workflow.</param>
/// <param name="Branch">Which branch was taken (telemetry).</param>
/// <param name="TopScore">Cosine similarity of the top-1 agent (0 if no embedder).</param>
/// <param name="Margin">Top-1 minus top-2 score (<see cref="float.MaxValue"/> if &lt; 2 agents).</param>
/// <param name="TopK">Raw top-K from the embedder, with scores. Empty if embedder unavailable.</param>
public readonly record struct DecisionTreeResult(
    IReadOnlyList<string> Candidates,
    BranchKind Branch,
    float TopScore,
    float Margin,
    IReadOnlyList<(string Name, float Score)> TopK);
