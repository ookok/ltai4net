using System.Collections.Concurrent;
using LTAI.AI;
using LTAI.Agent.Context;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

[ToolDomain("memory")]
public sealed class L3OnDemandProvider : AIContextProvider
{
    private const int MaxDrawers = 10;
    private readonly PalaceStore _store;
    private readonly EntropyTracker? _entropy;
    private readonly ILogger<L3OnDemandProvider>? _logger;
    private readonly ConcurrentDictionary<string, (DateTime Expiry, AIContext Context)> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    public L3OnDemandProvider(
        PalaceStore store,
        EntropyTracker? entropy = null,
        ILogger<L3OnDemandProvider>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _entropy = entropy;
        _logger = logger;
    }

    public override IReadOnlyList<string> StateKeys => ["L3OnDemand"];

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        if (context.AIContext.IsProviderSkipped("L3OnDemand"))
            return new AIContext();

        try
        {
            // Skip on-demand memory when ExpertRouterAgent already injected aggregated context
            var msgs = context.AIContext?.Messages;
            if (msgs != null)
            {
                foreach (var m in msgs.Reverse())
                {
                    if (m.Role == ChatRole.System && m.Text?.StartsWith("## Expert Context") == true)
                        return new AIContext();
                }
            }

            var wing = WingClassifier.ClassifyFromMessages(context.AIContext?.Messages);
            if (wing == null) return new AIContext();

            if (_cache.TryGetValue(wing, out var cached) && DateTime.UtcNow < cached.Expiry)
                return cached.Context;

            var maxDrawers = MaxDrawers + (int)((_entropy?.GetUncertaintyBoost(wing) ?? 0) * 10);
            var drawers = _store.SearchByWing(wing, Math.Max(MaxDrawers, maxDrawers));
            if (drawers.Count == 0) return new AIContext();

            var lines = new List<string> { $"## L3 — On-Demand ({wing})\n<memory>" };
            var totalLen = lines[0].Length;

            foreach (var d in drawers)
            {
                var snippet = MemoryCompressor.SmartTruncate(d.Content, 250);
                var entry = $"  [{d.Room}] {snippet}";

                if (totalLen + entry.Length > MemoryBudget.L3MaxTokens * 4) break;
                lines.Add(entry);
                totalLen += entry.Length;
            }
            lines.Add("</memory>");

            _logger?.LogDebug("L3OnDemand: {Count} drawers for wing={Wing}, ~{Tokens}t",
                drawers.Count, wing, totalLen / 4);
            var result = new AIContext
            {
                Messages = [new ChatMessage(ChatRole.System, string.Join("\n", lines))],
            };
            _cache[wing] = (DateTime.UtcNow + CacheTtl, result);
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "L3OnDemand: retrieval failed");
            return new AIContext();
        }
    }
}
