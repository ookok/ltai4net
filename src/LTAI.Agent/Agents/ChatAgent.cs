using System.Runtime.CompilerServices;
using LTAI.Agent.Workflows;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LTAI.Agent;

/// <summary>
/// Thin convenience wrapper. Supports direct chat + workflow delegation.
/// Real pipeline is in AIAgent (MAF).
/// </summary>
public sealed class ChatAgent
{
    private readonly AIAgent _agent;
    private readonly WorkflowOrchestrator? _workflows;
    private AgentSession? _session;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    public ChatAgent(AIAgent agent, WorkflowOrchestrator? workflows = null)
    {
        _agent = agent;
        _workflows = workflows;
    }

    public async Task<string> ChatAsync(string message, CancellationToken ct = default)
    {
        var session = await GetOrCreateSessionAsync(ct).ConfigureAwait(false);
        var r = await _agent.RunAsync(
            [new ChatMessage(ChatRole.User, message)], session, cancellationToken: ct).ConfigureAwait(false);
        return r.Messages?.LastOrDefault()?.Text ?? "";
    }

    public async IAsyncEnumerable<AgentResponseUpdate> ChatStreamingAsync(
        string message, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var session = await GetOrCreateSessionAsync(ct).ConfigureAwait(false);
        await foreach (var update in _agent.RunStreamingAsync(
            [new ChatMessage(ChatRole.User, message)], session, cancellationToken: ct).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <summary>
    /// Execute a handoff workflow: the orchestrator routes to specialist agents.
    /// </summary>
    public Task<AgentResponse> RunWorkflowAsync(string task, CancellationToken ct = default)
    {
        if (_workflows == null)
            return Task.FromResult(new AgentResponse(
                new ChatMessage(ChatRole.Assistant, "Workflow orchestrator not available.")));
        return _workflows.ExecuteHandoffAsync(task, ct: ct);
    }

    /// <summary>
    /// Execute agents sequentially.
    /// </summary>
    public Task<string> RunSequentialAsync(string[] agentNames, string task, CancellationToken ct = default)
    {
        if (_workflows == null)
            return Task.FromResult("Workflow orchestrator not available.");
        return _workflows.ExecuteSequentialAsync(agentNames, task, ct);
    }

    private async ValueTask<AgentSession> GetOrCreateSessionAsync(CancellationToken ct)
    {
        if (_session != null) return _session;
        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _session ??= await _agent.CreateSessionAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _sessionLock.Release();
        }
        return _session;
    }
}
