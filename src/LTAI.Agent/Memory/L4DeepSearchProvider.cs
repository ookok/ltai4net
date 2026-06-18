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

            // ── MeMo-inspired Multi-Turn Memory Retrieval ──
            // Phase 1: Grounding — retrieve broad context via hybrid search
            var groundingResults = await _store.HybridSearchAsync(queryVec, query, MaxDrawers * 3, wing)
                .ConfigureAwait(false);

            // Phase 2: Entity Identification — find specific entities from grounding context
            var entityLines = new List<string>();
            var entityDrawers = new List<(LTAI.Agent.Memory.PalaceStore.Drawer Drawer, double Score)>();
            foreach (var (drawer, score) in groundingResults)
            {
                if (score < effectiveMinSimilarity * 0.025) continue;

                // Check if this entry is an entity-surfacing companion (room ends with ".entity")
                if (drawer.Room != null && drawer.Room.EndsWith(".entity"))
                {
                    entityDrawers.Add((drawer, score));
                    continue;
                }

                var snippet = MemoryCompressor.SmartTruncate(drawer.Content, 300);
                var entry = $"  [{drawer.Wing}/{drawer.Room}] (rrf:{score:F3}) {snippet}";
                if (EntityPrefixSum(entityLines) + entry.Length > MemoryBudget.L4MaxTokens * 2) break;
                entityLines.Add(entry);
            }

            // Phase 3: Answer Synthesis — combine grounding + entity context
            var lines = new List<string> { "## L4 — Deep Search (Multi-Turn)\n<memory>" };
            var totalLen = lines[0].Length;

            // Add entity context first (from .entity rooms)
            if (entityDrawers.Count > 0)
            {
                lines.Add("  ### Entity Context (Reverse Lookup)");
                foreach (var (drawer, score) in entityDrawers.Take(3))
                {
                    var snippet = MemoryCompressor.SmartTruncate(drawer.Content, 200);
                    var entry = $"  [{drawer.Wing}/{drawer.Room}] {snippet}";
                    if (totalLen + entry.Length > MemoryBudget.L4MaxTokens * 3) break;
                    lines.Add(entry);
                    totalLen += entry.Length;
                }
                lines.Add("");
            }

            // Add grounding context
            foreach (var line in entityLines)
            {
                if (totalLen + line.Length > MemoryBudget.L4MaxTokens * 4) break;
                lines.Add(line);
                totalLen += line.Length;
            }

            // If we had entity results, add a cross-reference synthesis note
            if (entityDrawers.Count > 0)
            {
                lines.Add("");
                lines.Add("  ### Synthesis (Forward + Reverse)");
                lines.Add("  Memory retrieved from both forward query and reverse entity lookup.");
            }

            lines.Add("</memory>");

            // Include reflection entries (MeMo-style pre-synthesized QA)
            var reflections = _store.SearchByRoom("reflection")
                .Where(d => wing == null || string.Equals(d.Wing, wing, StringComparison.OrdinalIgnoreCase))
                .Take(3)
                .ToList();

            if (reflections.Count > 0)
            {
                lines.Add("\n  ### Related Reflections (Pre-synthesized)");
                foreach (var r in reflections)
                {
                    var snippet = MemoryCompressor.SmartTruncate(r.Content, 200);
                    if (totalLen + snippet.Length > MemoryBudget.L4MaxTokens * 4) break;
                    lines.Add($"  [{r.Wing}] {snippet}");
                    totalLen += snippet.Length;
                }
            }

            if (lines.Count == 2) return new AIContext();

            _logger?.LogDebug("L4DeepSearch: {Grounding} grounding, {Entity} entity, {Reflection} reflections, ~{Tokens}t",
                groundingResults.Count, entityDrawers.Count, reflections.Count, totalLen / 4);

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

    private static int EntityPrefixSum(List<string> lines) => lines.Sum(l => l.Length);
}
