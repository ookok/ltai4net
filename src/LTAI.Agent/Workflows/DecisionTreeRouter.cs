// Copyright (c) LTAI. All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LTAI.AI;
using Microsoft.Extensions.AI;
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
    private readonly IChatClient? _steer;
    private readonly RetryChainEmbedder? _retryChain;

    public DecisionTreeRouter(
        EmbeddingClient? embedder,
        ILogger<DecisionTreeRouter> logger,
        ToolEmbeddingCache? cache = null,
        DecisionTreeRouterOptions? options = null,
        YAMLWorkflowRegistry? registry = null,
        IChatClient? steer = null,
        RetryChainEmbedder? retryChain = null)
    {
        _embedder = embedder;
        _logger = logger;
        _cache = cache;
        _fallbackOptions = options ?? new DecisionTreeRouterOptions();
        _registry = registry;
        _steer = steer;
        _retryChain = retryChain;
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

        // Pre-allocate and reuse the names list to avoid repeated .ToArray() calls
        var allNamesList = (allSpecialistNames ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var allNamesArray = allNamesList.ToArray();
        var allNamesSet = new HashSet<string>(allNamesList, StringComparer.OrdinalIgnoreCase);

        // P15: apply candidate whitelist before any embedding work.
        if (whitelist.Count > 0)
        {
            var wl = new HashSet<string>(whitelist, StringComparer.OrdinalIgnoreCase);
            allNamesList = allNamesList.Where(n => wl.Contains(n)).ToList();
            allNamesArray = allNamesList.ToArray();
            allNamesSet = new HashSet<string>(allNamesList, StringComparer.OrdinalIgnoreCase);
            if (allNamesList.Count == 0)
            {
                _logger.LogWarning("Router: candidate whitelist filtered out all specialists ({N} requested, 0 matched)",
                    whitelist.Count);
                activity?.SetTag("router.branch", "NoCandidates");
                activity?.SetTag("router.whitelist_count", whitelist.Count);
                return new DecisionTreeResult([], BranchKind.NoCandidates, 0f, 0f, [], GetCurrentTier());
            }
        }

        // Stage 0: empty candidates → short-circuit before any embedding work
        if (allNamesList.Count == 0)
        {
            _logger.LogDebug("Router: no candidates (empty specialist list)");
            activity?.SetTag("router.branch", "NoCandidates");
            return new DecisionTreeResult([], BranchKind.NoCandidates, 0f, 0f, [], GetCurrentTier());
        }

        // Stage 0b: no embedder → use top-K (not all — P0.2 embedding fallback chain)
        if (_embedder is null)
        {
            var fallback = allNamesList.OrderBy(n => n).Take(topKLimit).ToList();
            _logger.LogDebug("Router: no embedder, using top-{K}/{N} specialists", fallback.Count, allNamesList.Count);
            activity?.SetTag("router.branch", "NoEmbedder");
            return new DecisionTreeResult(fallback, BranchKind.NoEmbedder, 0f, 0f, [], GetCurrentTier());
        }

        // Stage 1: top-K by cosine similarity
        var topK = await AgentRegistry
            .SelectTopKWithScoresAsync(task ?? "", _embedder, _cache, k: topKLimit, ct)
            .ConfigureAwait(false);

        if (topK.Count == 0)
        {
            var fallback = allNamesList.OrderBy(n => n).Take(topKLimit).ToList();
            _logger.LogWarning("Router: top-K returned empty, using top-{K}/{N} specialists", fallback.Count, allNamesList.Count);
            activity?.SetTag("router.branch", "EmbeddingFailed");
            return new DecisionTreeResult(fallback, BranchKind.EmbeddingFailed, 0f, 0f, [], GetCurrentTier());
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
            return new DecisionTreeResult([], BranchKind.NoConfidentMatch, topK[0].Score, 0f, topK, GetCurrentTier());
        }

        // Stage 2: confidence margin
        var topScore = topK[0].Score;
        var margin = topK.Count >= 2 ? topScore - topK[1].Score : float.MaxValue;

        // Stage 3: branch
        var confident = margin >= marginThreshold && topScore >= minScoreThreshold;

        if (confident)
        {
            var chosen = new List<string>(topKLimit);
            foreach (var t in topK)
            {
                if (allNamesSet.Contains(t.Name))
                    chosen.Add(t.Name);
            }

            if (chosen.Count == 0)
            {
                _logger.LogWarning("Router: embedding returned {K} candidates but none matched agent names — falling back to all {N}", topK.Count, allNamesList.Count);
                chosen = new List<string>(allNamesList);
            }

            _logger.LogInformation(
                "Router: CONFIDENT (margin={Margin:F3} ≥ {Threshold:F3}, top={Top:F3}) → top-{K} of {Total} (fallback={Fb})",
                margin, marginThreshold, topScore, chosen.Count, allNamesList.Count, fallbackKind);
            activity?.SetTag("router.branch", "ConfidentTopK");
            activity?.SetTag("router.top_score", topScore);
            activity?.SetTag("router.margin", margin);
            activity?.SetTag("router.chosen_count", chosen.Count);
            return new DecisionTreeResult(chosen, BranchKind.ConfidentTopK, topScore, margin, topK);
        }
        else
        {
            // P15: ambiguous branch honors ambiguousFallback strategy.
            // P6 Steer: when a steer LLM is available, use it to re-rank the top-K
            // candidates instead of falling back to ALL specialists. This saves
            // LLM context for the handoff workflow (fewer candidates = less routing overhead).
            IReadOnlyList<string> chosen2;
            BranchKind branch;
            switch (fallbackKind)
            {
                case AmbiguousFallbackKind.None:
                    chosen2 = [];
                    branch = BranchKind.NoConfidentMatch;
                    break;
                case AmbiguousFallbackKind.TopK:
                    var list = new List<string>(topK.Count);
                    foreach (var t in topK)
                    {
                        if (allNamesSet.Contains(t.Name))
                            list.Add(t.Name);
                    }
                    chosen2 = list;
                    branch = BranchKind.AmbiguousFallbackTopK;
                    break;
                default:
                    if (_steer != null)
                    {
                        chosen2 = await SteerRerankAsync(task!, topK, allNamesArray, ct).ConfigureAwait(false);
                        branch = BranchKind.AmbiguousFallback;
                    }
                    else
                    {
                        chosen2 = allNamesList;
                        branch = BranchKind.AmbiguousFallback;
                    }
                    break;
            }

            var reason = margin < marginThreshold
                ? $"margin={margin:F3} < {marginThreshold:F3}"
                : $"top={topScore:F3} < {minScoreThreshold:F3}";

            _logger.LogInformation(
                "Router: AMBIGUOUS ({Reason}) → {Branch} ({N} agents, fallback={Fb}, steer={HasSteer})",
                reason, branch, chosen2.Count, fallbackKind, _steer != null);
            activity?.SetTag("router.branch", branch.ToString());
            activity?.SetTag("router.top_score", topScore);
            activity?.SetTag("router.margin", margin);
            activity?.SetTag("router.ambiguous_reason", reason);
            activity?.SetTag("router.chosen_count", chosen2.Count);
            return new DecisionTreeResult(chosen2, branch, topScore, margin, topK);
        }
    }

    /// <summary>
    /// P6 Steer: Use the lightweight steer LLM to pick the best specialist from
    /// the top-K embedding candidates when the embedding margin is ambiguous.
    /// Returns at most top-2 agents chosen by the steer model.
    /// </summary>
    private EmbeddingTier GetCurrentTier()
    {
        if (_retryChain != null) return _retryChain.LastTier;
        if (_embedder?.Local?.Available == true) return AI.EmbeddingTier.Onnx;
        if (_embedder?.LocalFallbackActivated == true) return AI.EmbeddingTier.LocalFallback;
        if ((_embedder?.ConsecutiveAllProviderFailures ?? 0) > 1) return AI.EmbeddingTier.Bm25;
        return AI.EmbeddingTier.RemoteApi;
    }

        private async Task<string[]> SteerRerankAsync(
        string task,
        IReadOnlyList<(string Name, float Score)> topK,
        string[] allNames,
        CancellationToken ct)
    {
        var candidates = topK
            .Select(t => t.Name)
            .Where(n => allNames.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Take(5)
            .ToArray();

        if (candidates.Length <= 1) return candidates;

        var candidateList = string.Join("\n", candidates.Select((n, i) => $"{i + 1}. {n}"));
        var hasCjk = task.Any(c => c >= 0x4E00 && c <= 0x9FFF);
        var prompt = hasCjk
            ? $"""
            你是一个任务路由专家。根据用户任务，从以下候选 agent 中选择最合适的 1-2 个。
            只返回 JSON 数组，如 ["AgentName"] 或 ["Agent1", "Agent2"]。

            用户任务：{task}

            候选 agent：
            {candidateList}

            JSON：
            """
            : $"""
            You are a task routing expert. Given the user task, pick the best 1-2 agents from the candidates below.
            Return ONLY a JSON array, e.g. ["AgentName"] or ["Agent1", "Agent2"].

            User task: {task}

            Candidate agents:
            {candidateList}

            JSON:
            """;

        try
        {
            var chatOpts = new ChatOptions();
            // AdditionalProperties is pre-initialized by ChatOptions
            chatOpts.AdditionalProperties["response_format"] = new Dictionary<string, object>
            {
                ["type"] = "json_object"
            };

            var response = await _steer!.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)], chatOpts,
                cancellationToken: ct).ConfigureAwait(false);
            var text = response.Messages?.LastOrDefault()?.Text ?? "";

            // Parse JSON: handle markdown code fences, trailing text, leading text
            if (text.Contains('[') && text.Contains(']'))
            {
                var cleaned = text.Replace("```json", "").Replace("```", "").Trim();
                var start = cleaned.IndexOf('[');
                var end = cleaned.LastIndexOf(']');
                if (start >= 0 && end > start)
                {
                    var json = cleaned[start..(end + 1)];
                    var names = System.Text.Json.JsonSerializer.Deserialize<string[]>(json);
                    if (names is { Length: > 0 })
                        return names.Where(n => candidates.Contains(n, StringComparer.OrdinalIgnoreCase)).ToArray();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Steer re-rank failed, falling back to top-K");
        }

        // Fallback: return top-2 from embedding
        return candidates.Take(2).ToArray();
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
    public float MinAcceptableScore { get; init; } = 0.05f;
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
    IReadOnlyList<(string Name, float Score)> TopK,
    EmbeddingTier EmbeddingTier = AI.EmbeddingTier.Onnx);
