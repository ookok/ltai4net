using LTAI.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

[ToolDomain("memory")]
public sealed class L1EssentialProvider : AIContextProvider
{
    private const int MaxDrawers = 15;
    private const float MinImportance = 0.1f;
    private readonly PalaceStore _store;
    private readonly string _agentId;
    private readonly EntropyTracker? _entropy;
    private readonly ILogger<L1EssentialProvider>? _logger;
    private AIContext? _cached;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public L1EssentialProvider(
        PalaceStore store,
        string agentId = "default",
        EntropyTracker? entropy = null,
        ILogger<L1EssentialProvider>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _agentId = agentId;
        _entropy = entropy;
        _logger = logger;
    }

    public override IReadOnlyList<string> StateKeys => ["L1Essential"];

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        if (_cached != null && DateTime.UtcNow < _cacheExpiry)
            return ValueTask.FromResult(_cached);

        try
        {
            var moments = _store.GetEssentialMoments(MaxDrawers, _agentId);
            if (moments.Count == 0) return ValueTask.FromResult(new AIContext());

            var lines = new List<string> { "## L1 — Essential Story\n<memory>" };
            var totalLen = lines[0].Length;

            foreach (var d in moments)
            {
                // Entropy-driven importance floor: uncertain domains pull in lower-importance memories
                var effectiveMinImportance = (float)(MinImportance - Math.Max(0,
                    _entropy?.GetUncertaintyBoost(d.Wing) ?? 0) * 0.3);
                if (d.Importance < effectiveMinImportance) continue;

                var snippet = MemoryCompressor.SmartTruncate(d.Content, 200);
                var entry = $"  [{d.Wing}/{d.Room}] {snippet} (imp:{d.Importance:F1})";

                if (totalLen + entry.Length > MemoryBudget.L1MaxTokens * 4) break;
                lines.Add(entry);
                totalLen += entry.Length;
            }
            lines.Add("</memory>");

            _logger?.LogDebug("L1Essential: {Count} moments, ~{Tokens}t", moments.Count, totalLen / 4);
            var result = new AIContext
            {
                Messages = [new ChatMessage(ChatRole.System, string.Join("\n", lines))],
            };
            _cached = result;
            _cacheExpiry = DateTime.UtcNow + CacheTtl;
            return ValueTask.FromResult(result);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "L1Essential: retrieval failed");
            return ValueTask.FromResult(new AIContext());
        }
    }
}
