using LTAI.Agent.Context;
using LTAI.Agent.Delta;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Indexing;

public sealed class ProvenanceProvider : AIContextProvider
{
    private readonly ProvenanceTracker _tracker;
    private readonly CodeProvenanceIndex? _codeProvenance;

    public ProvenanceProvider(ProvenanceTracker tracker, CodeProvenanceIndex? codeProvenance = null) : base(null, null, null)
    {
        _tracker = tracker;
        _codeProvenance = codeProvenance;
    }

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        if (context.AIContext.IsProviderSkipped("ProvenanceProvider"))
            return context.AIContext ?? new AIContext();
        LookaheadProviderSelector.RecordProviderUsed("ProvenanceProvider");

        var recent = _tracker.List();
        var messages = new List<ChatMessage>();

        if (recent.Count > 0)
        {
            var lines = recent.Take(10).Select(e => $"  {e}");
            var msg = new ChatMessage(ChatRole.System,
                $"[知识溯源 - 最近 {Math.Min(recent.Count, 10)} 条操作]\n{string.Join("\n", lines)}");
            messages.Add(msg);
        }

        // Add code provenance for recently edited files
        if (_codeProvenance != null && recent.Count > 0)
        {
            var recentFiles = recent
                .Select(e => e.Key)
                .Where(k => File.Exists(k))
                .Distinct()
                .Take(3);

            foreach (var filePath in recentFiles)
            {
                try
                {
                    var summary = await _codeProvenance.BuildProvenanceSummaryAsync(filePath).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(summary))
                    {
                        messages.Add(new ChatMessage(ChatRole.System, summary));
                    }
                }
                catch { /* best-effort */ }
            }
        }

        var msgs = context.AIContext?.Messages?.ToList() ?? [];
        msgs.InsertRange(0, messages);

        return new AIContext
        {
            Instructions = context.AIContext?.Instructions,
            Messages = msgs,
        };
    }
}
