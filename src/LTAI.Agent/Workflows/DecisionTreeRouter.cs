// Copyright (c) LTAI. All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LTAI.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Workflows;

/// <summary>
/// P7.7 + P15 decision-tree router. Replaces the flat top-K selection with a
/// 3-stage confidence cascade that gives the MAF <c>HandoffWorkflowBuilder</c>
/// either a tight top-K candidate set (when embedding is confident) or all
/// specialists (when embedding is ambiguous).
/// </summary>
/// <remarks>
/// <para><b>Stages (P7.7, unchanged):</b></para>
/// <list type="number">
/// <item><description><b>Embedding top-K</b>: cosine similarity ranks all agents, take K (default 3).</description></item>
/// <item><description><b>Confidence margin</b>: <c>margin = score[0] - score[1]</c>.</description></item>
/// <item><description><b>Branch:</b> confident (margin ≥ threshold AND top-1 score ≥ threshold) → top-K; ambiguous → fallback per <see cref="AmbiguousFallbackKind"/>.</description></item>
/// </list>
/// <para>
/// <b>P15 hot-editable thresholds:</b> thresholds and candidate whitelist are
/// loaded from <c>.livingtree/workflows/decision-tree.json</c> via
/// <see cref="YAMLWorkflowRegistry"/>. The router queries the registry on
/// every call so changes apply within ~1s (FileSystemWatcher). The
/// <see cref="DecisionTreeRouterOptions"/> constructor parameter remains the
/// hardcoded fallback used when the JSON file is absent or fails to parse
/// (D68 / D69: preserve previous behavior).
/// </para>
/// </remarks>
public sealed class DecisionTreeRouter
{
    private static readonly ActivitySource Activity = new("LTAI.Router");

    private readonly EmbeddingClient? _embedder;
    private readonly ToolEmbeddingCache? _cache;
    private readonly ILogger<DecisionTreeRouter> _logger;
    private readonly DecisionTreeRouterOptions _fallbackOptions;
    private readonly YAMLWorkflowRegistry? _registry;

    public DecisionTreeRouter(
        EmbeddingClient? embedder,
        ILogger<DecisionTreeRouter> logger,
        ToolEmbeddingCache? cache = null,
        DecisionTreeRouterOptions? options = null,
        YAMLWorkflowRegistry? registry = null)
    {
        _embedder = embedder;
        _logger = logger;
        _cache = cache;
        _fallbackOptions = options ?? new DecisionTreeRouterOptions();
        _registry = registry;
    }

    /// <summary>
    /// Resolve the current effective config. Order:
    /// 1. JSON file via <see cref="YAMLWorkflowRegistry"/> (P15 hot path).
    /// 2. Constructor-supplied <see cref="DecisionTreeRouterOptions"/> (P7.7 hardcoded fallback).
    /// </summary>
    private (int TopK, float Margin, float MinScore, float MinAcceptable, AmbiguousFallbackKind Fallback, IReadOnlyList<string> Whitelist) ResolveEffectiveConfig()
    {
        if (_registry != null)
        {
            var cfg = _registry.GetDecisionTreeConfig("decision-tree");
            if (cfg.SourcePath != null)
            {
                return (cfg.TopK, cfg.ConfidenceMarginThreshold, cfg.MinTopScoreThreshold, cfg.MinAcceptableScore, cfg.FallbackKind, cfg.Candidates);
            }
        }
        return (_fallbackOptions.TopK, _fallbackOptions.ConfidenceMarginThreshold, _fallbackOptions.MinTopScoreThreshold,
                0.05f, AmbiguousFallbackKind.All, Array.Empty<string>());
    }

    /// <summary>
    /// Run the decision tree. Returns the chosen candidate names plus the
    /// diagnostics describing the branch taken.
    /// </summary>
    public async Task<DecisionTreeResult> RouteAsync(
        string task,
        IReadOnlyCollection<string> allSpecialistNames,
        CancellationToken ct = default)
    {
        using var activity = Activity.StartActivity("router.route", ActivityKind.Internal);
        activity?.SetTag("router.task_length", task?.Length ?? 0);
        activity?.SetTag("router.specialist_count", allSpecialistNames?.Count ?? 0);

        var (topKLimit, marginThreshold, minScoreThreshold, minAcceptable, fallbackKind, whitelist) = ResolveEffectiveConfig();
        var cfg = _registry?.GetDecisionTreeConfig("decision-tree") ?? DecisionTreeConfig.Default;

        var allNames = allSpecialistNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        // P15: apply candidate whitelist before any embedding work.
        if (whitelist.Count > 0)
        {
            var wl = new HashSet<string>(whitelist, StringComparer.OrdinalIgnoreCase);
            allNames = allNames.Where(n => wl.Contains(n)).ToArray();
            if (allNames.Length == 0)
            {
                _logger.LogWarning("Router: candidate whitelist filtered out all specialists ({N} requested, 0 matched)",
                    whitelist.Count);
                activity?.SetTag("router.branch", "NoCandidates");
                activity?.SetTag("router.whitelist_count", whitelist.Count);
                return new DecisionTreeResult(Array.Empty<string>(), BranchKind.NoCandidates, 0f, 0f, []);
            }
        }

        // Stage 0: no embedder → use all (legacy behavior)
        if (_embedder is null)
        {
            _logger.LogDebug("Router: no embedder, using all {N} specialists", allNames.Length);
            activity?.SetTag("router.branch", "NoEmbedder");
            return new DecisionTreeResult(allNames, BranchKind.NoEmbedder, 0f, 0f, []);
        }

        // Stage 1: top-K by cosine similarity
        var topK = await AgentRegistry
            .SelectTopKWithScoresAsync(task, _embedder, _cache, k: topKLimit, ct)
            .ConfigureAwait(false);

        if (topK.Count == 0)
        {
            _logger.LogWarning("Router: top-K returned empty, using all {N} specialists", allNames.Length);
            activity?.SetTag("router.branch", "EmbeddingFailed");
            return new DecisionTreeResult(allNames, BranchKind.EmbeddingFailed, 0f, 0f, []);
        }

        // P2: MinAcceptableScore — if even the highest score is too low, decline to route
        if (topK[0].Score < minAcceptable)
        {
            _logger.LogInformation(
                "Router: NO_CONFIDENT_MATCH (top={Top:F3} < minAcceptable={Min:F3}) — returning empty",
                topK[0].Score, minAcceptable);
            activity?.SetTag("router.branch", "NoConfidentMatch");
            activity?.SetTag("router.top_score", topK[0].Score);
            activity?.SetTag("router.min_acceptable", minAcceptable);
            return new DecisionTreeResult(Array.Empty<string>(), BranchKind.NoConfidentMatch, topK[0].Score, 0f, topK);
        }

        // Stage 2: confidence margin
        var topScore = topK[0].Score;
        var margin = topK.Count >= 2 ? topScore - topK[1].Score : float.MaxValue;

        // Stage 3: branch
        var confident = margin >= marginThreshold && topScore >= minScoreThreshold;

        if (confident)
        {
            var chosen = topK
                .Select(t => t.Name)
                .Where(n => allNames.Contains(n, StringComparer.OrdinalIgnoreCase))
                .ToArray();

            if (chosen.Length == 0) chosen = allNames;

            _logger.LogInformation(
                "Router: CONFIDENT (margin={Margin:F3} ≥ {Threshold:F3}, top={Top:F3}) → top-{K} of {Total} (fallback={Fb})",
                margin, marginThreshold, topScore, chosen.Length, allNames.Length, fallbackKind);
            activity?.SetTag("router.branch", "ConfidentTopK");
            activity?.SetTag("router.top_score", topScore);
            activity?.SetTag("router.margin", margin);
            activity?.SetTag("router.chosen_count", chosen.Length);
            return new DecisionTreeResult(chosen, BranchKind.ConfidentTopK, topScore, margin, topK);
        }
        else
        {
            // P15: ambiguous branch honors ambiguousFallback strategy.
            var (chosen2, branch) = fallbackKind switch
            {
                AmbiguousFallbackKind.None => (
                    Array.Empty<string>(),
                    BranchKind.NoConfidentMatch),
                AmbiguousFallbackKind.TopK => (
                    topK.Select(t => t.Name)
                        .Where(n => allNames.Contains(n, StringComparer.OrdinalIgnoreCase))
                        .ToArray(),
                    BranchKind.AmbiguousFallbackTopK),
                _ => (allNames, BranchKind.AmbiguousFallback),
            };

            var reason = margin < marginThreshold
                ? $"margin={margin:F3} < {marginThreshold:F3}"
                : $"top={topScore:F3} < {minScoreThreshold:F3}";

            _logger.LogInformation(
                "Router: AMBIGUOUS ({Reason}) → {Branch} ({N} agents, fallback={Fb})",
                reason, branch, chosen2.Length, fallbackKind);
            activity?.SetTag("router.branch", branch.ToString());
            activity?.SetTag("router.top_score", topScore);
            activity?.SetTag("router.margin", margin);
            activity?.SetTag("router.ambiguous_reason", reason);
            activity?.SetTag("router.chosen_count", chosen2.Length);
            return new DecisionTreeResult(chosen2, branch, topScore, margin, topK);
        }
    }
}

/// <summary>
/// Hardcoded fallback thresholds for <see cref="DecisionTreeRouter"/>. Used
/// only when <c>decision-tree.json</c> is absent or fails to parse (D68).
/// P7.7 defaults: 3 / 0.15 / 0.30. Overridden at runtime by the JSON file
/// loaded via <see cref="YAMLWorkflowRegistry"/>.
/// </summary>
public sealed class DecisionTreeRouterOptions
{
    public int TopK { get; init; } = 3;
    public float ConfidenceMarginThreshold { get; init; } = 0.15f;
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
    /// <summary>Margin was low; fell back to all specialists (default P7.7 behavior).</summary>
    AmbiguousFallback,
    /// <summary>P15: margin low + ambiguousFallback=topK; fell back to top-K.</summary>
    AmbiguousFallbackTopK,
    /// <summary>P15: margin low + ambiguousFallback=none; return no candidates.</summary>
    NoConfidentMatch,
    /// <summary>P15: candidate whitelist filtered out everything.</summary>
    NoCandidates,
}

/// <summary>Result of <see cref="DecisionTreeRouter.RouteAsync"/>.</summary>
public readonly record struct DecisionTreeResult(
    IReadOnlyList<string> Candidates,
    BranchKind Branch,
    float TopScore,
    float Margin,
    IReadOnlyList<(string Name, float Score)> TopK);
