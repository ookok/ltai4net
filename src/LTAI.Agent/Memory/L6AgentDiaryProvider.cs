using LTAI.AI;
using LTAI.Agent.Context;
using LTAI.Core.Safety;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

[ToolDomain("memory")]
public sealed class L6AgentDiaryProvider : AIContextProvider
{
    private const int MaxEntries = 5;
    private readonly PalaceStore _store;
    private readonly string _agentId;
    private readonly ILogger<L6AgentDiaryProvider>? _logger;

    public L6AgentDiaryProvider(
        PalaceStore store,
        string agentId = "default",
        ILogger<L6AgentDiaryProvider>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _agentId = agentId;
        _logger = logger;
    }

    public override IReadOnlyList<string> StateKeys => ["L6AgentDiary"];

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        if (context.AIContext.IsProviderSkipped("L6AgentDiary"))
            return ValueTask.FromResult(new AIContext());
        LookaheadProviderSelector.RecordProviderUsed("L6AgentDiary");

        try
        {
            var diary = _store.SearchByRoom("diary", _agentId, MaxEntries);
            if (diary.Count == 0) return ValueTask.FromResult(new AIContext());

            var lines = new List<string> { $"## L6 — Agent Diary ({_agentId})\n<memory>" };
            var totalLen = lines[0].Length;

            foreach (var d in diary)
            {
                var snippet = d.Content.Replace('\n', ' ').Trim();
                if (snippet.Length > 150) snippet = snippet[..147] + "...";
                var entry = $"  [{d.Wing}/{d.Room}] {snippet}";

                if (totalLen + entry.Length > MemoryBudget.L6MaxTokens * 4) break;
                lines.Add(entry);
                totalLen += entry.Length;
            }
            lines.Add("</memory>");

            _logger?.LogDebug("L6AgentDiary: {Count} entries, ~{Tokens}t", diary.Count, totalLen / 4);
            return ValueTask.FromResult(new AIContext
            {
                Messages = [new ChatMessage(ChatRole.System, string.Join("\n", lines))],
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "L6AgentDiary: retrieval failed");
            return ValueTask.FromResult(new AIContext());
        }
    }

    protected override async ValueTask StoreAIContextAsync(
        InvokedContext context, CancellationToken ct = default)
    {
        try
        {
            var text = string.Join(' ', context.ResponseMessages?.Select(m => m.Text) ?? []);
            if (string.IsNullOrWhiteSpace(text)) return;

            // F4: Skip diary entry if content is unsafe (secrets, PII, etc.)
            if (!SafetyRules.IsSafeByRules(text))
            {
                _logger?.LogDebug("L6AgentDiary: skipped unsafe content");
                return;
            }

            // Truncate long responses: store only first 200 chars as diary entry.
            // Full response is preserved in conversation history; the diary captures
            // the "gist" for future recall without filling the essential-story pool with
            // long code blocks.
            var summary = text.Length > 200 ? text[..197] + "..." : text;
            await _store.StoreAsync("diary", _agentId, summary,
                role: "assistant", importance: 0.3, agentId: _agentId,
                ttlMs: PalaceStore.DefaultTtlMs).ConfigureAwait(false);
            _logger?.LogDebug("L6AgentDiary: stored diary entry for {Agent}", _agentId);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "L6AgentDiary: persist failed");
        }
    }
}
