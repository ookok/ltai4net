using System.Collections.Concurrent;
using LTAI.AI;
using LTAI.Agent.Context;
using LTAI.Agent.Vector;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

[ToolDomain("memory")]
public sealed class L4DeepSearchProvider : AIContextProvider
{
    private const int DefaultMaxDrawers = 5;
    private const float MinSimilarity = 0.25f;
    private const int MaxScalingRounds = 3;
    private const double ScalingConfidenceThreshold = 0.5;
    private const int ScaleFactorPerRound = 2;

    private readonly PalaceStore _store;
    private readonly EmbeddingClient _embedder;
    private readonly EntropyTracker? _entropy;
    private readonly IChatClient? _verbalAnnotator;
    private readonly ILogger<L4DeepSearchProvider>? _logger;
    private readonly ConcurrentDictionary<int, (DateTime Expiry, AIContext Context)> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    private readonly MadDenoiser _madDenoiser;
    private readonly MemoryConflictResolver? _conflictResolver;
    private readonly MmrMemoryFilter? _mmrFilter;
    private readonly QueryAwareMemoryRouter _router;
    private readonly SubgraphExpansionService? _subgraphExpander;

    public L4DeepSearchProvider(
        PalaceStore store,
        EmbeddingClient embedder,
        EntropyTracker? entropy = null,
        IChatClient? verbalAnnotator = null,
        ILogger<L4DeepSearchProvider>? logger = null,
        MadDenoiser? madDenoiser = null,
        MemoryConflictResolver? conflictResolver = null,
        MmrMemoryFilter? mmrFilter = null,
        QueryAwareMemoryRouter? router = null,
        SubgraphExpansionService? subgraphExpander = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _entropy = entropy;
        _verbalAnnotator = verbalAnnotator;
        _logger = logger;
        _madDenoiser = madDenoiser ?? new MadDenoiser();
        _conflictResolver = conflictResolver;
        _mmrFilter = mmrFilter;
        _router = router ?? new QueryAwareMemoryRouter();
        _subgraphExpander = subgraphExpander;
    }

    public override IReadOnlyList<string> StateKeys => ["L4DeepSearch"];

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        if (context.AIContext.IsProviderSkipped("L4DeepSearch"))
            return new AIContext();
        LookaheadProviderSelector.RecordProviderUsed("L4DeepSearch");

        try
        {
            var query = string.Join('\n', (context.AIContext.Messages ?? [])
                .Where(m => !string.IsNullOrWhiteSpace(m.Text))
                .Select(m => m.Text));
            if (string.IsNullOrWhiteSpace(query)) return new AIContext();

            // Skip deep search when ExpertRouterAgent already injected aggregated context
            var msgs = context.AIContext?.Messages;
            if (msgs != null)
            {
                foreach (var m in msgs.Reverse())
                {
                    if (m.Role == ChatRole.System && m.Text?.StartsWith("## Expert Context") == true)
                        return new AIContext();
                }
            }

            var cacheKey = query.GetHashCode();
            if (_cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.Expiry)
                return cached.Context;

            var queryVec = await _embedder.GenerateAsync(query, ct).ConfigureAwait(false);
            var wing = WingClassifier.ClassifyFromMessages(context.AIContext?.Messages);

            var effectiveMinSimilarity = _entropy?.GetRoomThreshold(wing)
                ?? MinSimilarity;

            // ── Mandol Stage 0: Query-adaptive routing (§3.3.1) ──
            var route = _router.Route(query);

            // ── EvoEmbedding-inspired Single-Round Context-Aware Retrieval ──
            // augmented with Verbal-R3 relevance-guided test-time scaling
            var MaxDrawers = route.MaxDrawers;
            var (ranked, annotations) = await RetrieveWithVerbalScalingAsync(
                query, queryVec, wing, effectiveMinSimilarity, ct).ConfigureAwait(false);

            if (ranked.Count == 0) return new AIContext();

            // ── Mandol Stage 0b: Room-aware result boosting ──
            if (route.RoomBoosts is { Count: > 0 })
            {
                for (int i = 0; i < ranked.Count; i++)
                {
                    var (drawer, cs, rrf, tw, ann) = ranked[i];
                    foreach (var (pattern, boost) in route.RoomBoosts)
                    {
                        if (drawer.Room.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        {
                            ranked[i] = (drawer, cs * boost, rrf, tw, ann);
                            break;
                        }
                    }
                }
                ranked = ranked.OrderByDescending(r => r.CombinedScore).ToList();
            }

            // ── Mandol Stage 1: MAD denoising (arXiv:2606.29778 §3.3.2) ──
            // Use Verbal-R3 annotation score as semantic relevance (Mandol §3.3.2 S_ce),
            // fallback to CombinedScore when annotations unavailable.
            if (ranked.Count >= 3)
            {
                var denoised = _madDenoiser.Denoise(
                    ranked.Select(r => (r.Drawer, (double)(r.Annotation?.Score ?? (float)r.CombinedScore))).ToList());
                var denoisedIds = new HashSet<string>(denoised.Select(d => d.Drawer.DrawerId));
                ranked = ranked.Where(r => denoisedIds.Contains(r.Drawer.DrawerId)).ToList();
            }

            // ── Mandol Stage 2: Arbitration-based conflict resolution ──
            if (ranked.Count >= 2 && _conflictResolver != null)
            {
                var resolved = _conflictResolver.Resolve(
                    ranked.Select(r => (r.Drawer, r.CombinedScore)).ToList());
                var resolvedIds = new HashSet<string>(resolved.Select(r => r.Drawer.DrawerId));
                ranked = ranked.Where(r => resolvedIds.Contains(r.Drawer.DrawerId)).ToList();
            }

            // ── Mandol Stage 3: MMR diversity filter under token budget ──
            if (ranked.Count > 1 && _mmrFilter != null)
            {
                var mmrResults = _mmrFilter.Filter(
                    ranked.Select(r => (r.Drawer, r.CombinedScore)).ToList(),
                    query, route.BasicBudget);
                var mmrIds = new HashSet<string>(mmrResults.Select(r => r.Drawer.DrawerId));
                ranked = ranked.Where(r => mmrIds.Contains(r.Drawer.DrawerId)).ToList();
            }

            // ── Mandol Stage 4: Subgraph expansion for multi-hop evidence (§3.3.1) ──
            var expanded = new List<ExpandedEvidence>();
            if (ranked.Count > 0 && _subgraphExpander != null && route.EntityBudget > 0)
            {
                var drawers = ranked.Select(r => r.Drawer).ToList();
                expanded = await _subgraphExpander.ExpandAsync(drawers, query, ct)
                    .ConfigureAwait(false);
            }

            var lines = new List<string>
            {
                "## L4 — Deep Search (EvoEmbedding + Verbal-R3)\n<memory>"
            };
            var totalLen = lines[0].Length;

            foreach (var (drawer, combinedScore, rrfScore, temporalWeight, ann) in ranked)
            {
                var tierTag = temporalWeight >= 0.8 ? "🔥" : temporalWeight >= 0.5 ? "🕐" : "📜";
                var confidenceTag = ann?.Confidence switch
                {
                    AnnotationConfidence.High => "✓",
                    AnnotationConfidence.Medium => "~",
                    _ => "?"
                };
                var snippet = MemoryCompressor.SmartTruncate(drawer.Content, 250);
                var rationale = ann != null && !string.IsNullOrWhiteSpace(ann.Rationale)
                    ? $" | 分析:{ann.Rationale}"
                    : "";
                var entry = $"  {tierTag} [{confidenceTag}] [{drawer.Wing}/{drawer.Room}] (rrf:{rrfScore:F3} t:{temporalWeight:F2}){rationale} {snippet}";
                if (totalLen + entry.Length > MemoryBudget.L4MaxTokens * 4) break;
                lines.Add(entry);
                totalLen += entry.Length;
            }

            // Include reflection entries (pre-synthesized QA from MemoryRefinery)
            var reflections = _store.SearchByRoom("reflection")
                .Where(d => wing == null || string.Equals(d.Wing, wing, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToList();

            if (reflections.Count > 0)
            {
                lines.Add("\n  ### Related Reflections");
                foreach (var r in reflections)
                {
                    var snippet = MemoryCompressor.SmartTruncate(r.Content, 200);
                    if (totalLen + snippet.Length > MemoryBudget.L4MaxTokens * 4) break;
                    lines.Add($"  [{r.Wing}] {snippet}");
                    totalLen += snippet.Length;
                }
            }

            // Append expanded graph evidence
            if (expanded.Count > 0)
            {
                lines.Add("\n  ### Subgraph Expansion");
                foreach (var ev in expanded.Take(3))
                {
                    if (totalLen + ev.Content.Length > route.BasicBudget * 4) break;
                    lines.Add($"  [{ev.Source}] rel={ev.Relevance:F2} {ev.Content}");
                    totalLen += ev.Content.Length;
                }
            }

            lines.Add("</memory>");

            // Append Verbal-R3 annotation summary if available
            if (annotations != null && annotations.Annotations.Count > 0)
            {
                lines.Add("\n<verbal-annotations>");
                lines.Add($"  平均置信度: {annotations.AverageConfidence:P1}");
                lines.Add($"  高置信比例: {annotations.HighConfidenceRatio:P1}");
                if (annotations.AverageConfidence < ScalingConfidenceThreshold)
                {
                    lines.Add("  ⚠️ 整体置信偏低 — Generator 需额外验证或触发扩展搜索");
                }
                lines.Add("</verbal-annotations>");
                totalLen += 100;
            }

            if (lines.Count == 2) return new AIContext();

            _logger?.LogDebug("L4DeepSearch: {Count} results (Verbal-R3 scaled), ~{Tokens}t",
                ranked.Count, totalLen / 4);

            var result = new AIContext
            {
                Messages = [new ChatMessage(ChatRole.System, string.Join("\n", lines))],
            };
            _cache[cacheKey] = (DateTime.UtcNow + CacheTtl, result);
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "L4DeepSearch: retrieval failed");
            return new AIContext();
        }
    }

    /// <summary>
    /// Verbal-R3 relevance-guided test-time scaling.
    /// Iteratively expands search scope when top results have low annotation confidence.
    /// Reference: arXiv:2605.01399 (ACL 2026)
    /// </summary>
    private async Task<(
        List<(PalaceStore.Drawer Drawer, double CombinedScore, float RrfScore, double TemporalWeight, VerbalAnnotation? Annotation)>,
        VerbalAnnotationSet?)> RetrieveWithVerbalScalingAsync(
        string query, float[] queryVec, string? wing,
        float minSimilarity, CancellationToken ct)
    {
        var allResults = new List<PalaceStore.Drawer>();
        var seenIds = new HashSet<string>();
        int currentTopK = DefaultMaxDrawers * 3;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        for (int round = 0; round < MaxScalingRounds; round++)
        {
            // Round 0: hybrid search; subsequent rounds: expand candidate pool
            var rawResults = await _store.HybridSearchAsync(queryVec, query, currentTopK, wing)
                .ConfigureAwait(false);

            foreach (var r in rawResults)
            {
                if (seenIds.Add(r.Drawer.DrawerId ?? r.Drawer.Content.GetHashCode().ToString()))
                    allResults.Add(r.Drawer);
            }

            // Re-rank with temporal decay
            now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var ranked = allResults
                .Select(d =>
                {
                    var age = Math.Max(0, now - d.CreatedAt);
                    var temporalWeight = Math.Exp(-age / 300_000.0);
                    var importanceBoost = 1.0 + d.Importance * 0.5;
                    return (Drawer: d, TemporalWeight: temporalWeight, CombinedScore: temporalWeight * importanceBoost);
                })
                .OrderByDescending(x => x.CombinedScore)
                .Take(DefaultMaxDrawers * 2)
                .ToList();

            // Generate verbal annotations for top results
            if (_verbalAnnotator != null && ranked.Count > 0)
            {
                var annotationSet = await GenerateVerbalAnnotationsAsync(query, ranked, ct)
                    .ConfigureAwait(false);

                if (annotationSet.AverageConfidence >= ScalingConfidenceThreshold ||
                    annotationSet.HighConfidenceRatio >= 0.4)
                {
                    // Sufficient confidence — return results
                    var final = ranked
                        .Select(r => (
                            r.Drawer,
                            r.CombinedScore,
                            RrfScore: 0f,
                            r.TemporalWeight,
                            Annotation: annotationSet.Annotations.FirstOrDefault(
                                a =>                                 a.SourceId == r.Drawer.DrawerId)))
                        .ToList();
                    return (final, annotationSet);
                }

                // Low confidence — expand search scope
                _logger?.LogDebug("L4DeepSearch: Verbal-R3 scaling round {Round}/3 (avg confidence={Conf:P2})",
                    round + 1, annotationSet.AverageConfidence);
                currentTopK *= ScaleFactorPerRound;
            }
            else
            {
                // No annotator available — return results immediately
                var final = ranked
                    .Select(r => (r.Drawer, r.CombinedScore, RrfScore: 0f, r.TemporalWeight, Annotation: (VerbalAnnotation?)null))
                    .ToList();
                return (final, null);
            }
        }

        // Final round: return whatever we have with a warning
        var fallback = allResults
            .Select(d =>
            {
                var age = Math.Max(0, now - d.CreatedAt);
                var temporalWeight = Math.Exp(-age / 300_000.0);
                return (Drawer: d, CombinedScore: temporalWeight * (1.0 + d.Importance * 0.5),
                    RrfScore: 0f, TemporalWeight: temporalWeight, Annotation: (VerbalAnnotation?)null);
            })
            .OrderByDescending(x => x.CombinedScore)
            .Take(DefaultMaxDrawers)
            .ToList();
        return (fallback, null);
    }

    /// <summary>
    /// Generate Verbal-R3 verbal annotations for top-ranked memory items.
    /// Each annotation explains the logical connection between query and memory content.
    /// </summary>
    private async Task<VerbalAnnotationSet> GenerateVerbalAnnotationsAsync(
        string query,
        List<(PalaceStore.Drawer Drawer, double TemporalWeight, double CombinedScore)> items,
        CancellationToken ct)
    {
        if (_verbalAnnotator == null || items.Count == 0)
            return new VerbalAnnotationSet { Query = query };

        var annotations = new List<VerbalAnnotation>();

        if (items.Count == 1)
        {
            annotations.Add(new VerbalAnnotation
            {
                Score = 0.5f,
                Rationale = "单一结果，无法对比分析",
                Confidence = AnnotationConfidence.Medium,
                SourceId = items[0].Drawer.DrawerId
            });
            return new VerbalAnnotationSet { Query = query, Annotations = annotations };
        }

                var itemLines = items.Select((item, i) =>
                {
                    var preview = MemoryCompressor.SmartTruncate(item.Drawer.Content, 150);
                    return $"[{i + 1}] (score={item.CombinedScore:F3}) {preview}";
                });

        var prompt = $@"Analyze the relevance of each memory item to the user query.

User Query: {query}

Memory Items:
{string.Join("\n", itemLines)}

For each item, output a JSON array:
[{{""score"": <0-10>, ""rationale"": ""why this memory is relevant to the query"", ""confidence"": ""low|medium|high"", ""suggestion"": ""how to use this item""}}]";

        try
        {
            var response = await _verbalAnnotator
                .GetResponseAsync([new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(response?.Text))
                return new VerbalAnnotationSet { Query = query };

            var text = response.Text.Trim();
            var startIdx = text.IndexOf('[');
            var endIdx = text.LastIndexOf(']');
            if (startIdx >= 0 && endIdx > startIdx)
            {
                text = text[startIdx..(endIdx + 1)];
                var parsed = System.Text.Json.JsonSerializer.Deserialize<List<AnnotationResponseItem>>(text,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (parsed != null)
                {
                    for (int i = 0; i < parsed.Count && i < items.Count; i++)
                    {
                        annotations.Add(new VerbalAnnotation
                        {
                            Score = (float)Math.Clamp(parsed[i].Score / 10.0, 0, 1),
                            Rationale = parsed[i].Rationale ?? "",
                            Confidence = ParseConfidence(parsed[i].Confidence),
                            Suggestion = parsed[i].Suggestion,
                            SourceId = items[i].Drawer.DrawerId
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "L4DeepSearch: Verbal-R3 annotation failed (non-fatal)");
        }

        return new VerbalAnnotationSet { Query = query, Annotations = annotations };
    }

    private static AnnotationConfidence ParseConfidence(string? confidence)
        => confidence?.ToLowerInvariant() switch
        {
            "high" => AnnotationConfidence.High,
            "medium" => AnnotationConfidence.Medium,
            "low" => AnnotationConfidence.Low,
            _ => AnnotationConfidence.Medium
        };

    private sealed record AnnotationResponseItem(double Score, string? Rationale, string? Confidence, string? Suggestion);
}
