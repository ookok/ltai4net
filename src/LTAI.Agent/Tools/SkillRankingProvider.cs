using System.Collections.Concurrent;
using LTAI.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Tools;

/// <summary>
/// L1 Skill Evolution: MAF AIContextProvider that re-ranks tools by success-rate weighting.
/// Runs after <see cref="ToolRetrievalProvider"/> to apply evolution-derived boosts.
/// </summary>
public sealed class SkillRankingProvider : AIContextProvider
{
    private readonly SkillEvolutionEngine _engine;
    private readonly ToolResultCapturingChatClient _capturingClient;
    private readonly ILogger<SkillRankingProvider> _logger;

    public SkillRankingProvider(
        SkillEvolutionEngine engine,
        ToolResultCapturingChatClient capturingClient,
        ILogger<SkillRankingProvider> logger) : base(null, null, null)
    {
        _engine = engine;
        _capturingClient = capturingClient;
        _logger = logger;
    }

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
            Instructions = existing.Instructions,
            Messages = existing.Messages,
            Tools = reRanked
        });
    }
}
