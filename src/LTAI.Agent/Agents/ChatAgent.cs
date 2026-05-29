using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LTAI.Agent;

/// <summary>Thin convenience wrapper. Real pipeline is in AIAgent (MAF).</summary>
public sealed class ChatAgent
{
    private readonly AIAgent _agent;
    private AgentSession? _session;

    public ChatAgent(AIAgent agent) => _agent = agent;

    public async Task<string> ChatAsync(string message, CancellationToken ct = default)
    {
        _session ??= await _agent.CreateSessionAsync(ct);
        var r = await _agent.RunAsync(
            [new ChatMessage(ChatRole.User, message)], _session, cancellationToken: ct);
        return r.Messages?.LastOrDefault()?.Text ?? "";
    }

    public IAsyncEnumerable<AgentResponseUpdate> ChatStreamingAsync(string message, CancellationToken ct = default)
    {
        _session ??= _agent.CreateSessionAsync(ct).GetAwaiter().GetResult();
        return _agent.RunStreamingAsync(
            [new ChatMessage(ChatRole.User, message)], _session, cancellationToken: ct);
    }
}
