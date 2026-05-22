using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Agents;

public sealed class ReasoningAgent : AIAgent
{
    private readonly ILogger<ReasoningAgent> _logger;
    private readonly int _maxSearchDepth;

    public override string Name { get; }
    public override string Description { get; }

    public ReasoningAgent(
        IChatClient chatClient,
        AgentCard card,
        IEnumerable<AITool> reasoningTools,
        ILogger<ReasoningAgent> logger)
        : base(new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = card.Name,
            Description = card.Instructions
        }))
    {
        Name = card.Name;
        Description = card.Instructions;
        _logger = logger;

        _maxSearchDepth = card.Options.TryGetValue("maxSearchDepth", out var d) && d is int depth ? depth : 5;

        foreach (var tool in reasoningTools)
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
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, "No reasoning request received."));

        var query = userMsg.Text ?? "";

        var reasoningPrompt = $"""
            You are a deep reasoning agent. Think step by step. 
            
            Use the following reasoning strategy:
            1. Decompose the problem into sub-problems
            2. For each sub-problem, explore up to {_maxSearchDepth} alternative solutions
            3. Evaluate each solution path by expected outcome
            4. Select the best solution and verify it
            
            Question: {query}
            
            Provide your reasoning chain and final answer.
            """;

        var enhancedMessages = new List<ChatMessage>(msgList.Take(msgList.Count - 1))
        {
            new(ChatRole.User, reasoningPrompt)
        };

        _logger.LogInformation("ReasoningAgent [{Name}]: Deep reasoning, maxDepth={Depth}", Name, _maxSearchDepth);

        var response = await _agent.RunAsync(enhancedMessages, session, options, cancellationToken);

        _logger.LogInformation("ReasoningAgent [{Name}]: Reasoning complete", Name);
        return response;
    }
}
