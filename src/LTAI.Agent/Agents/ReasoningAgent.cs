using System.Runtime.CompilerServices;
using System.Text.Json;
using LTAI.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Agents;

public sealed class ReasoningAgent : AIAgent
{
    private readonly ChatClientAgent _inner;
    private readonly ILogger<ReasoningAgent> _logger;
    private readonly int _maxSearchDepth;

    public override string Name { get; }
    public override string Description { get; }

    public ReasoningAgent(
        IChatClient chatClient,
        LTAIAgentCard card,
        IEnumerable<Microsoft.Extensions.AI.AITool> reasoningTools,
        ILogger<ReasoningAgent> logger)
    {
        Name = card.Name;
        Description = card.Instructions;
        _logger = logger;

        _maxSearchDepth = card.Options.TryGetValue("maxSearchDepth", out var d) && d is int depth ? depth : 5;

        _inner = chatClient.AsBuilder().BuildAIAgent(new ChatClientAgentOptions
        {
            Name = card.Name,
            Description = card.Instructions,
            ChatOptions = new() { Tools = reasoningTools.ToList() }
        });
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

        var response = await _inner.RunAsync(enhancedMessages, session, options, cancellationToken);

        _logger.LogInformation("ReasoningAgent [{Name}]: Reasoning complete", Name);
        return response;
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in _inner.RunStreamingAsync(messages, session, options, cancellationToken))
            yield return update;
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        => _inner.CreateSessionAsync(cancellationToken);

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => _inner.SerializeSessionAsync(session, jsonSerializerOptions, cancellationToken);

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => _inner.DeserializeSessionAsync(serializedState, jsonSerializerOptions, cancellationToken);
}
