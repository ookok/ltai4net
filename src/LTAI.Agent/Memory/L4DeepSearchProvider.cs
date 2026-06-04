using LTAI.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

[ToolDomain("memory")]
public sealed class L4DeepSearchProvider : AIContextProvider
{
    private const int MaxTokens = 2000;
    private const int MaxDrawers = 5;
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
            var query = string.Join('\n', context.AIContext.Messages
                .Where(m => !string.IsNullOrWhiteSpace(m.Text))
                .Select(m => m.Text));
            if (string.IsNullOrWhiteSpace(query)) return new AIContext();

            var queryVec = await _embedder.GenerateAsync(query, ct).ConfigureAwait(false);
            var wing = InferWing(context);

            var lines = new List<string> { "## L4 — Deep Search" };
            var totalLen = lines[0].Length;

            await foreach (var (drawer, score) in _store.SemanticSearchAsync(queryVec, MaxDrawers, wing, ct).ConfigureAwait(false))
            {
                var snippet = drawer.Content.Replace('\n', ' ').Trim();
                if (snippet.Length > 300) snippet = snippet[..297] + "...";
                var entry = $"  [{drawer.Wing}/{drawer.Room}] (sim:{score:F2}) {snippet}";

                if (totalLen + entry.Length > MaxTokens * 4) break;
                lines.Add(entry);
                totalLen += entry.Length;
            }

            if (lines.Count == 1) return new AIContext();

            _logger?.LogDebug("L4DeepSearch: {Count} results, ~{Tokens}t", lines.Count - 1, totalLen / 4);
            return new AIContext
            {
                Messages = [new ChatMessage(ChatRole.User, string.Join("\n", lines))],
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "L4DeepSearch: retrieval failed");
            return new AIContext();
        }
    }

    private static string? InferWing(InvokingContext ctx)
    {
        var text = string.Join(' ', ctx.AIContext.Messages
            .Where(m => !string.IsNullOrWhiteSpace(m.Text))
            .Select(m => m.Text));
        if (string.IsNullOrWhiteSpace(text)) return null;

        var knownWings = new[] { "project", "code", "user", "system", "architecture", "config" };
        foreach (var w in knownWings)
            if (text.Contains(w, StringComparison.OrdinalIgnoreCase))
                return w;
        return null;
    }
}
