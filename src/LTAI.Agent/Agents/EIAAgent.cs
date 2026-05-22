using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Agents;

public sealed class EIAAgent : AIAgent
{
    private readonly ILogger<EIAAgent> _logger;

    public override string Name { get; }
    public override string Description { get; }

    public EIAAgent(
        IChatClient chatClient,
        AgentCard card,
        IEnumerable<AITool> eiaTools,
        ILogger<EIAAgent> logger)
        : base(new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = card.Name,
            Description = card.Instructions
        }))
    {
        Name = card.Name;
        Description = card.Instructions;
        _logger = logger;

        foreach (var tool in eiaTools)
            Tools.Add(tool);
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var msgList = messages.ToList();
        var userMsg = msgList.LastOrDefault(m => m.Role == ChatRole.User);

        if (userMsg is null)
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, "No environmental assessment request received."));

        _logger.LogInformation("EIAAgent [{Name}]: Processing EIA request", Name);

        var response = await _agent.RunAsync(messages, session, options, cancellationToken);

        _logger.LogInformation("EIAAgent [{Name}]: Assessment complete", Name);
        return response;
    }
}
