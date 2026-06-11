using System.Collections.Concurrent;
using LTAI.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Tools;

/// <summary>
/// L1 Skill Evolution: MAF AIContextProvider that re-ranks tools by success-rate weighting.
/// Runs first in the LTAI AIContextProvider chain to apply evolution-derived boosts.
/// (Tool filtering has moved to <c>ToolFilteringChatClient</c>, an IChatClient middleware.)
/// </summary>
public sealed class SkillRankingProvider : AIContextProvider
{
    private readonly SkillEvolutionEngine _engine;
    private readonly ILogger<SkillRankingProvider> _logger;

    public SkillRankingProvider(
        SkillEvolutionEngine engine,
        ILogger<SkillRankingProvider> logger) : base(null, null, null)
    {
        _engine = engine;
        _logger = logger;
    }

    // Override InvokingCoreAsync to REPLACE tools instead of concatenating.
    // MAF base class merges via a.Concat(b), which doubles the tool list.
#pragma warning disable MAAI001 // Experimental
    protected override async ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        var inputContext = context.AIContext;

        var filteredContext = new InvokingContext(
            context.Agent,
            context.Session,
            new AIContext
            {
                Instructions = inputContext.Instructions,
                Messages = inputContext.Messages,
                Tools = inputContext.Tools
            });

        var provided = await ProvideAIContextAsync(filteredContext, cancellationToken).ConfigureAwait(false);

        var mergedInstructions = (inputContext.Instructions, provided.Instructions) switch
        {
            (null, null) => null,
            (string a, null) => a,
            (null, string b) => b,
            (string a, string b) => a + "\n" + b
        };

        var providedMessages = provided.Messages is not null
            ? provided.Messages.Select(m => m.WithAgentRequestMessageSource(
                AgentRequestMessageSourceType.AIContextProvider, GetType().FullName!))
            : null;

        var mergedMessages = (inputContext.Messages, providedMessages) switch
        {
            (null, null) => null,
            (var a, null) => a,
            (null, var b) => b,
            (var a, var b) => a.Concat(b)
        };

        // REPLACE tools: SkillRankingProvider re-orders the current set,
        // it should not double the list via base-class concatenation.
        var mergedTools = provided.Tools ?? inputContext.Tools;

        return new AIContext
        {
            Instructions = mergedInstructions,
            Messages = mergedMessages,
            Tools = mergedTools
        };
    }
#pragma warning restore MAAI001

    protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken ct = default)
    {
        var existing = context.AIContext;
        if (existing?.Tools is null) return new ValueTask<AIContext>(existing!);

        // Re-rank tools: boost high-success tools, demote low-success tools
        var reRanked = existing.Tools
            .Select(t => (tool: t, boost: _engine.GetRankBoost(t.Name ?? "")))
            .OrderByDescending(x => x.boost)
            .Select(x => x.tool)
            .ToList();

        _logger.LogDebug("[SkillRanking] Re-ranked {Count} tools", reRanked.Count);

        return new ValueTask<AIContext>(new AIContext
        {
            Tools = reRanked
        });
    }
}
