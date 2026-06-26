using System.Collections.Concurrent;
using LTAI.AI;
using LTAI.Agent.Context;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

[ToolDomain("memory")]
public sealed class L4DeepSearchProvider : AIContextProvider
{
    private const int MaxDrawers = 5;
    private const float MinSimilarity = 0.25f;
    private readonly PalaceStore _store;
    private readonly EmbeddingClient _embedder;
    private readonly EntropyTracker? _entropy;
    private readonly ILogger<L4DeepSearchProvider>? _logger;
    private readonly ConcurrentDictionary<int, (DateTime Expiry, AIContext Context)> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    public L4DeepSearchProvider(
        PalaceStore store,
        EmbeddingClient embedder,
        EntropyTracker? entropy = null,
        ILogger<L4DeepSearchProvider>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _entropy = entropy;
        _logger = logger;
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

            // ── EvoEmbedding-inspired Single-Round Context-Aware Retrieval ──
            // Replaces the previous 3-phase protocol (Grounding → Entity Identification → Synthesis)
            // with a single hybrid search + temporal decay re-ranking.
            // The key insight (arXiv:2606.21649): evolvable embeddings make simple
            // retrieval competitive with complex multi-round protocols.
            var rawResults = await _store.HybridSearchAsync(queryVec, query, MaxDrawers * 3, wing)
                .ConfigureAwait(false);

            // Context-aware re-ranking: apply temporal decay + relevance boost
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var ranked = rawResults
                .Select(r =>
                {
                    // Temporal decay: recent memories weighted higher (half-life = 5 min)
                    var age = Math.Max(0, now - r.Drawer.CreatedAt);
                    var temporalWeight = Math.Exp(-age / 300_000.0);
                    // Importance boost for high-importance entries
                    var importanceBoost = 1.0 + r.Drawer.Importance * 0.5;
                    // Entity surface boost: .entity rooms contain reverse-lookup QA
                    var entityBoost = r.Drawer.Room?.EndsWith(".entity") == true ? 1.2 : 1.0;
                    // Combined score
                    var combined = r.Score * temporalWeight * importanceBoost * entityBoost;
                    return (r.Drawer, CombinedScore: combined, r.Score, TemporalWeight: temporalWeight);
                })
                .Where(x => x.CombinedScore >= effectiveMinSimilarity * 0.02)
                .OrderByDescending(x => x.CombinedScore)
                .Take(MaxDrawers * 2)
                .ToList();

            if (ranked.Count == 0) return new AIContext();

            var lines = new List<string> { "## L4 — Deep Search (EvoEmbedding)\n<memory>" };
            var totalLen = lines[0].Length;

            foreach (var (drawer, combinedScore, rrfScore, temporalWeight) in ranked)
            {
                var tierTag = temporalWeight >= 0.8 ? "🔥" : temporalWeight >= 0.5 ? "🕐" : "📜";
                var snippet = MemoryCompressor.SmartTruncate(drawer.Content, 300);
                var entry = $"  {tierTag} [{drawer.Wing}/{drawer.Room}] (rrf:{rrfScore:F3} t:{temporalWeight:F2}) {snippet}";
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

            lines.Add("</memory>");

            if (lines.Count == 2) return new AIContext();

            _logger?.LogDebug("L4DeepSearch: {Count} results (single-round, temporal-decay re-ranked), ~{Tokens}t",
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

}
