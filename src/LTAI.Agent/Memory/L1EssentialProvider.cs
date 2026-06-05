using LTAI.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

[ToolDomain("memory")]
public sealed class L1EssentialProvider : AIContextProvider
{
    private const int MaxDrawers = 15;
    private readonly PalaceStore _store;
    private readonly string _agentId;
    private readonly ILogger<L1EssentialProvider>? _logger;

    public L1EssentialProvider(
        PalaceStore store,
        string agentId = "default",
        ILogger<L1EssentialProvider>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _agentId = agentId;
        _logger = logger;
    }

    public override IReadOnlyList<string> StateKeys => ["L1Essential"];

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        try
        {
            var moments = _store.GetEssentialMoments(MaxDrawers, _agentId);
            if (moments.Count == 0) return ValueTask.FromResult(new AIContext());

            var lines = new List<string> { "## L1 — Essential Story\n<memory>" };
            var totalLen = lines[0].Length;

            foreach (var d in moments)
            {
                var snippet = d.Content.Replace('\n', ' ').Trim();
                if (snippet.Length > 200) snippet = snippet[..197] + "...";
                var entry = $"  [{d.Wing}/{d.Room}] {snippet} (imp:{d.Importance:F1})";

                if (totalLen + entry.Length > MemoryBudget.L1MaxTokens * 4) break;
                lines.Add(entry);
                totalLen += entry.Length;
            }
            lines.Add("</memory>");

            _logger?.LogDebug("L1Essential: {Count} moments, ~{Tokens}t", moments.Count, totalLen / 4);
            return ValueTask.FromResult(new AIContext
            {
                Messages = [new ChatMessage(ChatRole.System, string.Join("\n", lines))],
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "L1Essential: retrieval failed");
            return ValueTask.FromResult(new AIContext());
        }
    }
}
