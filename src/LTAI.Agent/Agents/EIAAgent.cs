using System.Runtime.CompilerServices;
using System.Text.Json;
using LTAI.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Agents;

public sealed class EIAAgent : AIAgent
{
    private readonly ChatClientAgent _inner;
    private readonly ILogger<EIAAgent> _logger;

    public override string Name { get; }
    public override string Description { get; }

    public EIAAgent(
        IChatClient chatClient,
        LTAIAgentCard card,
        IEnumerable<Microsoft.Extensions.AI.AITool> eiaTools,
        ILogger<EIAAgent> logger)
    {
        Name = card.Name;
        Description = card.Instructions;
        _logger = logger;

        _inner = chatClient.AsBuilder().BuildAIAgent(new ChatClientAgentOptions
        {
            Name = card.Name,
            Description = card.Instructions,
            ChatOptions = new() { Tools = eiaTools.ToList() }
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
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, "No environmental assessment request received."));

        _logger.LogInformation("EIAAgent [{Name}]: Processing EIA request", Name);

        var response = await _inner.RunAsync(messages, session, options, cancellationToken);

        _logger.LogInformation("EIAAgent [{Name}]: Assessment complete", Name);
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
