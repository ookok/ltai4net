using LTAI.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

[ToolDomain("memory")]
public sealed class L3OnDemandProvider : AIContextProvider
{
    private const int MaxTokens = 500;
    private const int MaxDrawers = 10;
    private readonly PalaceStore _store;
    private readonly ILogger<L3OnDemandProvider>? _logger;

    public L3OnDemandProvider(
        PalaceStore store,
        ILogger<L3OnDemandProvider>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger;
    }

    public override IReadOnlyList<string> StateKeys => ["L3OnDemand"];

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        try
        {
            var wing = InferWing(context);
            if (wing == null) return new AIContext();

            var drawers = _store.SearchByWing(wing, MaxDrawers);
            if (drawers.Count == 0) return new AIContext();

            var lines = new List<string> { $"## L3 — On-Demand ({wing})" };
            var totalLen = lines[0].Length;

            foreach (var d in drawers)
            {
                var snippet = d.Content.Replace('\n', ' ').Trim();
                if (snippet.Length > 250) snippet = snippet[..247] + "...";
                var entry = $"  [{d.Room}] {snippet}";

                if (totalLen + entry.Length > MaxTokens * 4) break;
                lines.Add(entry);
                totalLen += entry.Length;
            }

            _logger?.LogDebug("L3OnDemand: {Count} drawers for wing={Wing}, ~{Tokens}t",
                drawers.Count, wing, totalLen / 4);
            return new AIContext
            {
                Messages = [new ChatMessage(ChatRole.User, string.Join("\n", lines))],
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "L3OnDemand: retrieval failed");
            return new AIContext();
        }
    }

    private static string? InferWing(InvokingContext ctx)
    {
        var text = string.Join(' ', (ctx.AIContext.Messages ?? [])
            .Where(m => !string.IsNullOrWhiteSpace(m.Text))
            .Select(m => m.Text));
        if (string.IsNullOrWhiteSpace(text)) return null;

        var knownWings = new[] { "project", "code", "user", "system", "architecture", "config" };
        foreach (var w in knownWings)
        {
            if (text.Contains(w, StringComparison.OrdinalIgnoreCase))
                return w;
        }
        return null;
    }
}
