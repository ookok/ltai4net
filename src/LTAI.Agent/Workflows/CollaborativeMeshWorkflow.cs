using LTAI.Agent.Routing;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Workflows;

public sealed class CollaborativeMeshWorkflow
{
    private readonly ILogger<CollaborativeMeshWorkflow> _logger;
    private readonly IntentRouter _router;
    private readonly Dictionary<string, AIAgent> _agents = new();
    private AIAgent? _entryAgent;
    private int _maxRounds;
    private const int DefaultMaxRounds = 5;

    public CollaborativeMeshWorkflow(ILogger<CollaborativeMeshWorkflow> logger, IntentRouter router, int maxRounds = DefaultMaxRounds)
    {
        _logger = logger;
        _router = router;
        _maxRounds = maxRounds;
    }

    public void SetEntryAgent(AIAgent agent)
    {
        _entryAgent = agent;
        _agents["chat"] = agent;
    }

    public void RegisterAgent(string name, AIAgent agent)
    {
        _agents[name] = agent;
    }

    public void SetMaxRounds(int rounds)
    {
        _maxRounds = Math.Max(1, rounds);
    }

    public Workflow BuildHandoffWorkflow()
    {
        if (_entryAgent is null)
            throw new InvalidOperationException("Entry agent not set. Call SetEntryAgent first.");

        var builder = AgentWorkflowBuilder.CreateHandoffBuilderWith(_entryAgent)
            .WithName("LTAI Collaborative Handoff Mesh")
            .WithDescription("Multi-agent collaborative handoff workflow with intent-based routing");

        foreach (var (name, agent) in _agents)
        {
            if (name == "chat") continue;
            builder.WithHandoff(_entryAgent, agent, $"Transfer to {name} agent for {name}-specific tasks");
        }

        _logger.LogInformation("CollaborativeMeshWorkflow: Built handoff workflow with {Count} agents", _agents.Count);
        return builder.Build();
    }

    public Workflow BuildSequentialWorkflow(params string[] agentSequence)
    {
        if (agentSequence.Length < 2)
            throw new ArgumentException("Sequential workflow requires at least 2 agents", nameof(agentSequence));

        var resolved = new List<AIAgent>();
        foreach (var name in agentSequence)
        {
            if (_agents.TryGetValue(name, out var a))
                resolved.Add(a);
            else
                throw new InvalidOperationException($"Agent '{name}' not registered.");
        }

        var builder = new WorkflowBuilder(resolved[0]);
        for (var i = 0; i < resolved.Count - 1; i++)
            builder.AddEdge(resolved[i], resolved[i + 1]);

        builder.WithOutputFrom(resolved[^1]);

        _logger.LogInformation("CollaborativeMeshWorkflow: Built sequential workflow [{Seq}]",
            string.Join(" → ", agentSequence));
        return builder.Build();
    }

    public Workflow BuildFanOutWorkflow(params string[] agentNames)
    {
        if (agentNames.Length < 2)
            throw new ArgumentException("Fan-out workflow requires at least 2 agents", nameof(agentNames));

        if (_entryAgent is null)
            throw new InvalidOperationException("Entry agent not set. Call SetEntryAgent first.");

        var resolved = agentNames
            .Select(n => _agents.TryGetValue(n, out var a) ? a : throw new InvalidOperationException($"Agent '{n}' not registered."))
            .ToList();

        var builder = new WorkflowBuilder(_entryAgent);
        foreach (var agent in resolved)
            builder.AddEdge(_entryAgent, agent);

        builder.WithOutputFrom(resolved[0]);

        _logger.LogInformation("CollaborativeMeshWorkflow: Built fan-out workflow [{Agents}]",
            string.Join(", ", agentNames));
        return builder.Build();
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
            _logger.LogWarning("CollaborativeMeshWorkflow: Max handoff depth {Depth} reached, preventing Ping-Pong loop", depth);
            return new AgentResponse(new ChatMessage(ChatRole.Assistant,
                $"[Workflow] Maximum handoff depth ({MaxHandoffDepth}) reached. Please rephrase your request."));
        }

        var msgList = messages.ToList();
        var userMsg = msgList.LastOrDefault(m => m.Role == ChatRole.User);

        if (userMsg?.Text is null)
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, "No message to process."));

        var route = _router.Classify(userMsg.Text);

        _logger.LogInformation("CollaborativeMeshWorkflow: Route intent={Intent} agent={Agent} conf={Conf:F2} keywords=[{Kw}] depth={Depth}",
            route.Intent, route.TargetAgent, route.Confidence, string.Join(", ", route.MatchedKeywords), depth);

        if (route.Confidence < 0.3f)
        {
            _logger.LogWarning("CollaborativeMeshWorkflow: Low confidence route, falling back to chat");
            return await FallbackToChat(messages, session, cancellationToken);
        }

        if (_agents.TryGetValue(route.TargetAgent, out var targetAgent))
        {
            var response = await targetAgent.RunAsync(messages, session, null, cancellationToken);

            // Check if response indicates handoff request
            if (response.Text?.Contains("[HANDOFF:", StringComparison.OrdinalIgnoreCase) == true)
            {
                _logger.LogInformation("CollaborativeMeshWorkflow: Agent requested handoff, depth={Depth}", depth + 1);

                // Summarize context before handoff
                var contextSummary = SummarizeContextForHandoff(userMsg.Text, response.Text);
                var handoffMessages = new List<ChatMessage>(messages)
                {
                    new(ChatRole.System, $"[Handoff Context from {route.TargetAgent}]: {contextSummary}")
                };

                return await RouteAndExecuteWithDepthAsync(handoffMessages, session, depth + 1, cancellationToken);
            }

            return await MaybeRunCriticAsync(route.TargetAgent, response, msgList, session, cancellationToken);
        }

        return await FallbackToChat(messages, session, cancellationToken);
    }

    public async Task<AgentResponse> RouteMultiIntentAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        CancellationToken cancellationToken = default)
    {
        var msgList = messages.ToList();
        var userMsg = msgList.LastOrDefault(m => m.Role == ChatRole.User);

        if (userMsg?.Text is null)
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, "No message to process."));

        var routes = _router.ClassifyAll(userMsg.Text);

        if (routes.Count <= 1)
            return await RouteAndExecuteAsync(messages, session, cancellationToken);

        var primary = routes[0];
        if (primary.Confidence < 0.3f)
            return await FallbackToChat(messages, session, cancellationToken);

        var responses = new List<string>();
        var round = 0;

        foreach (var route in routes.Take(3))
        {
            if (round >= _maxRounds) break;
            round++;

            if (_agents.TryGetValue(route.TargetAgent, out var agent))
            {
                var response = await agent.RunAsync(messages, session, null, cancellationToken);
                if (response.Text is not null)
                    responses.Add($"[{route.TargetAgent}]: {response.Text}");
            }
        }

        var combined = string.Join("\n\n", responses);
        return new AgentResponse(new ChatMessage(ChatRole.Assistant, combined));
    }

    private async Task<AgentResponse> MaybeRunCriticAsync(
        string agentName,
        AgentResponse originalResponse,
        List<ChatMessage> messages,
        AgentSession? session,
        CancellationToken cancellationToken)
    {
        var criticName = $"{agentName}_critic";
        if (!_agents.TryGetValue(criticName, out var critic))
            return originalResponse;

        _logger.LogInformation("CollaborativeMeshWorkflow: Running critic '{Critic}' for '{Agent}'", criticName, agentName);

        var reviewMessages = new List<ChatMessage>
        {
            new(ChatRole.User, $"Review the following {agentName} output for quality, compliance, and factual accuracy:\n\n{originalResponse.Text}")
        };

        var review = await critic.RunAsync(reviewMessages, session, null, cancellationToken);

        var originalText = originalResponse.Text ?? "";
        var reviewText = review.Text ?? "";

        var combined = $"{originalText}\n\n---\n## Critic Review ({criticName})\n{reviewText}";
        return new AgentResponse(new ChatMessage(ChatRole.Assistant, combined));
    }

    private async Task<AgentResponse> FallbackToChat(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        CancellationToken cancellationToken)
    {
        if (_agents.TryGetValue("chat", out var chatAgent))
            return await chatAgent.RunAsync(messages, session, null, cancellationToken);
        return new AgentResponse(new ChatMessage(ChatRole.Assistant, "No agent available to handle this request."));
    }

    private static string SummarizeContextForHandoff(string userQuery, string agentResponse)
    {
        var querySummary = userQuery.Length > 200 ? userQuery[..200] + "..." : userQuery;
        var responseSummary = agentResponse.Length > 300 ? agentResponse[..300] + "..." : agentResponse;

        return $"User asked: {querySummary}\n\nPrevious agent responded: {responseSummary}\n\nContinuing with handoff...";
    }
}
