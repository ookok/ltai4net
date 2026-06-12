using System.Collections.Concurrent;
using LTAI.AI;
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

            // Short TTL cache: skip ONNX embedding + semantic search if same query repeated within 5s
            var cacheKey = query.GetHashCode();
            if (_cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.Expiry)
                return cached.Context;

            var queryVec = await _embedder.GenerateAsync(query, ct).ConfigureAwait(false);
            var wing = WingClassifier.ClassifyFromMessages(context.AIContext?.Messages);

            // Entropy-driven + modality-aware threshold: different memory wings
            // have different natural similarity floors. Uncertainty lowers the bar.
            var effectiveMinSimilarity = _entropy?.GetRoomThreshold(wing)
                ?? MinSimilarity;

            var lines = new List<string> { "## L4 并行辅助模型（长江苦力四号）\n<memory>" };
            var totalLen = lines[0].Length;

            await foreach (var (drawer, score) in _store.SemanticSearchAsync(queryVec, MaxDrawers, wing).ConfigureAwait(false))
            {
                if (score < effectiveMinSimilarity) continue;

                var snippet = MemoryCompressor.SmartTruncate(drawer.Content, 300);
                var entry = $"  [{drawer.Wing}/{drawer.Room}] (sim:{score:F2}) {snippet}";

                if (totalLen + entry.Length > MemoryBudget.L4MaxTokens * 4) break;
                lines.Add(entry);
                totalLen += entry.Length;
            }

            lines.Add("</memory>");
            if (lines.Count == 2) return new AIContext();

            _logger?.LogDebug("L4DeepSearch: {Count} results, ~{Tokens}t", lines.Count - 1, totalLen / 4);
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
