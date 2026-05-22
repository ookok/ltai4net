using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Agents;

public sealed class CodeAgent : AIAgent
{
    private readonly ILogger<CodeAgent> _logger;

    public override string Name { get; }
    public override string Description { get; }

    public CodeAgent(
        IChatClient chatClient,
        AgentCard card,
        IEnumerable<AITool> tools,
        ILogger<CodeAgent> logger)
        : base(new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = card.Name,
            Description = card.Instructions
        }))
    {
        Name = card.Name;
        Description = card.Instructions;
        _logger = logger;

        foreach (var tool in tools)
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
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, "No code analysis request received."));

        _logger.LogInformation("CodeAgent [{Name}]: Analyzing code request", Name);

        var response = await _agent.RunAsync(messages, session, options, cancellationToken);

        _logger.LogInformation("CodeAgent [{Name}]: Analysis complete", Name);
        return response;
    }
}
