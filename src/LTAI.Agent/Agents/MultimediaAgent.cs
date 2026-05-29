using Microsoft.Agents.AI; using Microsoft.Extensions.AI; using Microsoft.Extensions.Logging;
namespace LTAI.Agent.Agents;
public sealed class MultimediaAgent : BaseAgent
{
    public MultimediaAgent(AgentCard card, IChatClient brain, SkillRegistry skills, ILogger<MultimediaAgent> logger) : base(card, brain, skills, logger) { }
    protected override async Task<AgentResponse> ExecuteLogicAsync(AgentContext context, CancellationToken ct) => await CallBrainAsync(context.FullHistory, ct: ct);
}
