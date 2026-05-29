using LTAI.Agent.MAF;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Agents;

public sealed class ChatAgent
{
    private readonly LTAIAgent _agent;
    public ChatAgent(LTAIAgent agent) => _agent = agent;

    public async Task<string> ChatAsync(string message, AgentSession? session = null, CancellationToken ct = default)
    {
        var r = await _agent.RunAsync([new ChatMessage(ChatRole.User, message)], session, cancellationToken: ct).ConfigureAwait(false);
        return r.Messages?.LastOrDefault()?.Text ?? "";
    }

    public IAsyncEnumerable<AgentResponseUpdate> ChatStreamingAsync(string message, AgentSession? s = null, CancellationToken ct = default)
        => _agent.RunStreamingAsync([new ChatMessage(ChatRole.User, message)], s, cancellationToken: ct);
}
