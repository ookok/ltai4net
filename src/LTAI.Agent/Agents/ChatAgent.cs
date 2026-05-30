using System.Runtime.CompilerServices;
using LTAI.Agent.Workflows;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LTAI.Agent;

/// <summary>
/// Thin convenience wrapper. Supports direct chat + workflow delegation.
/// Default uses L1 (flash) model. Complex tasks auto-upgrade to L2 (pro).
/// </summary>
public sealed class ChatAgent
{
    private readonly AIAgent _agent;
    private readonly AIAgent _proAgent;
    private readonly WorkflowOrchestrator? _workflows;
    private AgentSession? _session;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    /// <param name="agent">Default L1 (flash) agent.</param>
    /// <param name="proAgent">L2 (pro) agent for complex task auto-upgrade. Falls back to agent if null.</param>
    /// <param name="workflows">Optional workflow orchestrator.</param>
    public ChatAgent(AIAgent agent, AIAgent? proAgent = null, WorkflowOrchestrator? workflows = null)
    {
        _agent = agent;
        _proAgent = proAgent ?? agent;
        _workflows = workflows;
    }

    public async Task<string> ChatAsync(string message, CancellationToken ct = default)
    {
        var session = await GetOrCreateSessionAsync(ct).ConfigureAwait(false);
        var messages = new[] { new ChatMessage(ChatRole.User, message) };

        // L1: try with flash model first
        var r = await _agent.RunAsync(messages, session, cancellationToken: ct).ConfigureAwait(false);
        var text = r.Messages?.LastOrDefault()?.Text ?? "";

        // L2: detect upgrade marker, re-run with pro model
        if (text.Contains("<<<NEEDS_PRO:"))
        {
            // Extract reason for logging
            var reason = "complex task";
            var match = System.Text.RegularExpressions.Regex.Match(text, @"<<<NEEDS_PRO:\s*(.+?)>>>");
            if (match.Success) reason = match.Groups[1].Value.Trim();

            // Re-run with pro agent
            r = await _proAgent.RunAsync(messages, session, cancellationToken: ct).ConfigureAwait(false);
            text = r.Messages?.LastOrDefault()?.Text ?? "";

            // Prepend upgrade note
            text = $"[Auto-upgraded to Pro: {reason}]\n\n{text}";
        }

        return text;
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
