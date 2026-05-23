using LTAI.Agent.Routing;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

#pragma warning disable MAAIW001 // Experimental API

namespace LTAI.Agent.Workflows;

public sealed class HandoffMeshWorkflow
{
    private readonly ILogger<HandoffMeshWorkflow> _logger;
    private readonly IntentRouter _router;
    private readonly Dictionary<string, AIAgent> _agents = new();
    private AIAgent? _initialAgent;
    private Workflow? _cachedWorkflow;

    public HandoffMeshWorkflow(ILogger<HandoffMeshWorkflow> logger, IntentRouter router)
    {
        _logger = logger;
        _router = router;
    }

    public void SetInitialAgent(AIAgent agent)
    {
        _initialAgent = agent;
        _agents["initial"] = agent;
        _cachedWorkflow = null;
    }

    public void RegisterAgent(string name, AIAgent agent)
    {
        _agents[name] = agent;
        _cachedWorkflow = null;
    }

    public Workflow Build()
    {
        if (_cachedWorkflow is not null)
            return _cachedWorkflow;

        if (_initialAgent is null)
            throw new InvalidOperationException("Initial agent not set");

        var builder = AgentWorkflowBuilder.CreateHandoffBuilderWith(_initialAgent)
            .WithName("LTAI Handoff Mesh")
            .WithDescription("Multi-agent handoff workflow with intent-based routing");

        foreach (var (name, agent) in _agents)
        {
            if (name == "initial") continue;
            builder.WithHandoff(_initialAgent, agent,
                $"Transfer to {name} agent for {name}-related queries");
        }

        _logger.LogInformation("HandoffMeshWorkflow built with {Count} agents", _agents.Count);
        return _cachedWorkflow = builder.Build();
    }

    public async Task<AgentResponse> RouteAndExecuteAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        CancellationToken cancellationToken = default)
    {
        return await RouteAndExecuteWithDepthAsync(messages, session, 0, cancellationToken);
    }

    private async Task<AgentResponse> RouteAndExecuteWithDepthAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        int depth,
        CancellationToken cancellationToken)
    {
        const int MaxHandoffDepth = 3;

        if (depth >= MaxHandoffDepth)
        {
            _logger.LogWarning("HandoffMeshWorkflow: Max handoff depth {Depth} reached, preventing Ping-Pong loop", depth);
            return new AgentResponse(new ChatMessage(ChatRole.Assistant,
                $"[Workflow] Maximum handoff depth ({MaxHandoffDepth}) reached. Please rephrase your request."));
        }

        var msgList = messages.ToList();
        var userMsg = msgList.LastOrDefault(m => m.Role == ChatRole.User);

        if (userMsg?.Text is null)
        {
            return new AgentResponse(new ChatMessage(ChatRole.Assistant,
                "No message to process."));
        }

        var route = _router.Classify(userMsg.Text);

        _logger.LogInformation("HandoffMeshWorkflow: intent={Intent} -> agent={Agent} conf={Conf:F2} depth={Depth}",
            route.Intent, route.TargetAgent, route.Confidence, depth);

        if (_agents.TryGetValue(route.TargetAgent, out var targetAgent) && route.Confidence >= 0.3f)
        {
            var response = await targetAgent.RunAsync(messages, session, null, cancellationToken);

            // Check if response indicates handoff request
            if (response.Text?.Contains("[HANDOFF:", StringComparison.OrdinalIgnoreCase) == true)
            {
                _logger.LogInformation("HandoffMeshWorkflow: Agent requested handoff, depth={Depth}", depth + 1);
                return await RouteAndExecuteWithDepthAsync(messages, session, depth + 1, cancellationToken);
            }

            return response;
        }

        if (_agents.TryGetValue("chat", out var chatAgent))
            return await chatAgent.RunAsync(messages, session, null, cancellationToken);

        return new AgentResponse(new ChatMessage(ChatRole.Assistant,
            "No agent available to handle this request."));
    }

    public async IAsyncEnumerable<AgentResponseUpdate> RouteAndExecuteStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var msgList = messages.ToList();
        var userMsg = msgList.LastOrDefault(m => m.Role == ChatRole.User);
        var route = userMsg?.Text is not null ? _router.Classify(userMsg.Text) : null;
        var agentName = route?.TargetAgent ?? "chat";
        var targetAgent = _agents.GetValueOrDefault(agentName)
            ?? _agents.GetValueOrDefault("chat")
            ?? _initialAgent;

        if (targetAgent is null)
            yield break;

        await foreach (var update in targetAgent.RunStreamingAsync(
            messages, session, null, cancellationToken))
        {
            yield return update;
        }
    }
}
