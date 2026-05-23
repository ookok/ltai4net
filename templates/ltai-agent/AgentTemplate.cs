using System.Runtime.CompilerServices;
using System.Text.Json;
using LTAI.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Agents;

public sealed class AgentTemplate : BaseAgent
{
    public AgentTemplate(
        LTAIAgentCard card,
        IChatClient brain,
        SkillRegistry skills,
        ILogger<AgentTemplate> logger)
        : base(card, brain, skills, logger)
    {
        RegisterStrategy(new DefaultAgentTemplateStrategy(brain, logger));
    }

    protected override async Task<AgentResponse> ExecuteLogicAsync(
        AgentContext context, CancellationToken ct)
    {
        _logger.LogInformation("AgentTemplate [{Name}]: processing", Name);

        var messages = new List<ChatMessage>(context.FullHistory)
        {
            new(ChatRole.User, context.UserQuery)
        };

        return await CallBrainAsync(messages, ct: ct);
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in CallBrainStreamingAsync(messages, cancellationToken))
            yield return update;
    }
}

internal sealed class DefaultAgentTemplateStrategy : IAnalysisStrategy<AgentContext, AgentResponse>
{
    private readonly IChatClient _brain;
    private readonly ILogger _logger;

    public string StrategyName => "default";

    public DefaultAgentTemplateStrategy(IChatClient brain, ILogger logger)
    {
        _brain = brain;
        _logger = logger;
    }

    public bool CanHandle(string query) => true;

    public async Task<AgentResponse> AnalyzeAsync(AgentContext context, CancellationToken ct)
    {
        var response = await _brain.GetResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, context.UserQuery) },
            cancellationToken: ct);
        return new AgentResponse(new ChatMessage(ChatRole.Assistant, response.Text ?? ""));
    }
}
