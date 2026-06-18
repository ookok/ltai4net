using LTAI.Agent.Context;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Indexing;

public sealed class ProvenanceProvider : AIContextProvider
{
    private readonly ProvenanceTracker _tracker;

    public ProvenanceProvider(ProvenanceTracker tracker) : base(null, null, null)
    {
        _tracker = tracker;
    }

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        if (context.AIContext.IsProviderSkipped("ProvenanceProvider"))
            return ValueTask.FromResult(context.AIContext ?? new AIContext());
        LookaheadProviderSelector.RecordProviderUsed("ProvenanceProvider");

        var recent = _tracker.List();
        if (recent.Count == 0)
            return ValueTask.FromResult(context.AIContext ?? new AIContext());

        var lines = recent.Take(10).Select(e => $"  {e}");
        var msg = new ChatMessage(ChatRole.System,
            $"[知识溯源 - 最近 {Math.Min(recent.Count, 10)} 条操作]\n{string.Join("\n", lines)}");

        var msgs = context.AIContext?.Messages?.ToList() ?? [];
        msgs.Insert(0, msg);

        return ValueTask.FromResult(new AIContext
        {
            Instructions = context.AIContext?.Instructions,
            Messages = msgs,
        });
    }
}
