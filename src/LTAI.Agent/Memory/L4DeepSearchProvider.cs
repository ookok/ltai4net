using LTAI.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

[ToolDomain("memory")]
public sealed class L4DeepSearchProvider : AIContextProvider
{
    private const int MaxDrawers = 5;
    private const float MinSimilarity = 0.25f; // F9: confidence floor — skip low-similarity results
    private readonly PalaceStore _store;
    private readonly EmbeddingClient _embedder;
    private readonly ILogger<L4DeepSearchProvider>? _logger;

    public L4DeepSearchProvider(
        PalaceStore store,
        EmbeddingClient embedder,
        ILogger<L4DeepSearchProvider>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
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

            var queryVec = await _embedder.GenerateAsync(query, ct).ConfigureAwait(false);
            var wing = WingClassifier.ClassifyFromMessages(context.AIContext?.Messages);

            var lines = new List<string> { "## L4 并行辅助模型（长江苦力四号）\n<memory>" };
            var totalLen = lines[0].Length;

            await foreach (var (drawer, score) in _store.SemanticSearchAsync(queryVec, MaxDrawers, wing).ConfigureAwait(false))
            {
                // F9: confidence floor — skip low-similarity results
                if (score < MinSimilarity) continue;

                var snippet = drawer.Content.Replace('\n', ' ').Trim();
                if (snippet.Length > 300) snippet = snippet[..297] + "...";
                var entry = $"  [{drawer.Wing}/{drawer.Room}] (sim:{score:F2}) {snippet}";

                if (totalLen + entry.Length > MemoryBudget.L4MaxTokens * 4) break;
                lines.Add(entry);
                totalLen += entry.Length;
            }

            lines.Add("</memory>");
            if (lines.Count == 2) return new AIContext();

            _logger?.LogDebug("L4DeepSearch: {Count} results, ~{Tokens}t", lines.Count - 1, totalLen / 4);
            return new AIContext
            {
                Messages = [new ChatMessage(ChatRole.System, string.Join("\n", lines))],
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "L4DeepSearch: retrieval failed");
            return new AIContext();
        }
    }
}
