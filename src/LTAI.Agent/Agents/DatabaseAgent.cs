using Microsoft.Agents.AI; using Microsoft.Extensions.AI; using Microsoft.Extensions.Logging;
namespace LTAI.Agent.Agents;
public sealed class DatabaseAgent : BaseAgent
{
    public DatabaseAgent(AgentCard card, IChatClient brain, SkillRegistry skills, ILogger<DatabaseAgent> logger) : base(card, brain, skills, logger) { }
    protected override async Task<AgentResponse> ExecuteLogicAsync(AgentContext context, CancellationToken ct) => await CallBrainAsync(context.FullHistory, ct: ct);
}
