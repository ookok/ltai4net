using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

#pragma warning disable MAAIW001 // Experimental API

namespace LTAI.Agent.Workflows;

public sealed class HandoffMeshWorkflow
{
    private readonly ILogger<HandoffMeshWorkflow> _logger;
    private readonly Dictionary<string, AIAgent> _agents = new();
    private AIAgent? _initialAgent;
    private Workflow? _cachedWorkflow;

    public HandoffMeshWorkflow(ILogger<HandoffMeshWorkflow> logger)
    {
        _logger = logger;
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
            .WithDescription("Multi-agent handoff workflow with keyword-based routing");

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
        var msgList = messages.ToList();
        var userMsg = msgList.LastOrDefault(m => m.Role == ChatRole.User);

        if (userMsg?.Text is null)
        {
            return new AgentResponse(new ChatMessage(ChatRole.Assistant,
                "No message to process."));
        }

        var intent = ClassifyRouteIntent(userMsg.Text);
        var targetAgent = GetBestAgent(intent);

        _logger.LogInformation("HandoffMeshWorkflow: intent={Intent} -> agent={Agent}",
            intent, targetAgent?.Name ?? "fallback");

        if (targetAgent is not null)
            return await targetAgent.RunAsync(messages, session, null, cancellationToken);

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
        var intent = userMsg?.Text is not null ? ClassifyRouteIntent(userMsg.Text) : "chat";
        var targetAgent = GetBestAgent(intent)
            ?? _agents.GetValueOrDefault("chat")
            ?? _initialAgent;

        if (targetAgent is null)
        {
            yield break;
        }

        await foreach (var update in targetAgent.RunStreamingAsync(
            messages, session, null, cancellationToken))
        {
            yield return update;
        }
    }

    private AIAgent? GetBestAgent(string intent)
    {
        return intent switch
        {
            "code" => _agents.GetValueOrDefault("code"),
            "eia" => _agents.GetValueOrDefault("eia"),
            "reasoning" => _agents.GetValueOrDefault("reasoning"),
            _ => _agents.GetValueOrDefault("chat")
        };
    }

    private static string ClassifyRouteIntent(string text)
    {
        var lower = text.ToLowerInvariant();

        if (lower.Contains("code") || lower.Contains("programming") || lower.Contains("class ") ||
            lower.Contains("function ") || lower.Contains("debug") || lower.Contains("build") ||
            lower.Contains("test") || lower.Contains("refactor"))
            return "code";

        if (lower.Contains("环境") || lower.Contains("impact") || lower.Contains("emission") ||
            lower.Contains("environmental") || lower.Contains("gis") || lower.Contains("map") ||
            lower.Contains("spatial") || lower.Contains("ecological"))
            return "eia";

        if (lower.Contains("analyze") || lower.Contains("reason") || lower.Contains("think") ||
            lower.Contains("compare") || lower.Contains("evaluate") || lower.Contains("solve") ||
            lower.Contains("logic") || lower.Contains("为什么") || lower.Contains("如何"))
            return "reasoning";

        return "chat";
    }
}
