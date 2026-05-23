using System.Diagnostics;
using LTAI.Agent.Routing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Workflows;

public sealed class AgentMeshWorkflow
{
    private static readonly ActivitySource ActivitySource = new("LTAI.Agent.Mesh");

    private readonly ILogger<AgentMeshWorkflow> _logger;
    private readonly IntentRouter _router;
    private readonly Dictionary<string, AIAgent> _agents = new();

    public AgentMeshWorkflow(ILogger<AgentMeshWorkflow> logger, IntentRouter router)
    {
        _logger = logger;
        _router = router;
    }

    public void RegisterAgent(string name, AIAgent agent)
    {
        _agents[name] = agent;
        _logger.LogInformation("AgentMeshWorkflow: Registered agent '{Name}'", name);
    }

    public IReadOnlyDictionary<string, AIAgent> Agents => _agents;

    public async Task<AgentResponse> RouteAndExecuteAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        CancellationToken cancellationToken = default)
    {
        using var rootSpan = ActivitySource.StartActivity("mesh.route", ActivityKind.Server);

        var msgList = messages.ToList();
        var userMsg = msgList.LastOrDefault(m => m.Role == ChatRole.User);

        if (userMsg?.Text is null)
        {
            rootSpan?.SetStatus(ActivityStatusCode.Error, "No user message");
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, "No message to process."));
        }

        var route = _router.Classify(userMsg.Text);
        rootSpan?.SetTag("mesh.intent", route.Intent);
        rootSpan?.SetTag("mesh.target_agent", route.TargetAgent);
        rootSpan?.SetTag("mesh.confidence", route.Confidence);

        _logger.LogInformation("AgentMeshWorkflow: Routing intent={Intent} agent={Agent} conf={Conf:F2}",
            route.Intent, route.TargetAgent, route.Confidence);

        if (_agents.TryGetValue(route.TargetAgent, out var targetAgent) && route.Confidence >= 0.3f)
        {
            using var agentSpan = ActivitySource.StartActivity($"agent.{route.TargetAgent}.run");
            agentSpan?.SetTag("agent.name", route.TargetAgent);
            agentSpan?.SetTag("agent.intent", route.Intent);

            var response = await targetAgent.RunAsync(messages, session, null, cancellationToken);
            agentSpan?.SetTag("agent.response_length", response.Text?.Length ?? 0);

            var criticResponse = await MaybeRunCriticAsync(route.TargetAgent, response, msgList, session, cancellationToken);

            rootSpan?.SetStatus(ActivityStatusCode.Ok);
            return criticResponse;
        }

        rootSpan?.SetTag("mesh.fallback", true);

        if (_agents.TryGetValue("chat", out var chatAgent))
        {
            using var fallbackSpan = ActivitySource.StartActivity("agent.chat.fallback");
            var response = await chatAgent.RunAsync(messages, session, null, cancellationToken);
            rootSpan?.SetStatus(ActivityStatusCode.Ok);
            return response;
        }

        rootSpan?.SetStatus(ActivityStatusCode.Error, "No agent available");
        return new AgentResponse(new ChatMessage(ChatRole.Assistant, "No agent available to handle this request."));
    }

    public async Task<AgentResponse> RouteMultiIntentAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        CancellationToken cancellationToken = default)
    {
        using var span = ActivitySource.StartActivity("mesh.multi-intent");

        var msgList = messages.ToList();
        var userMsg = msgList.LastOrDefault(m => m.Role == ChatRole.User);
        if (userMsg?.Text is null)
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, "No message to process."));

        var routes = _router.ClassifyAll(userMsg.Text);
        if (routes.Count <= 1)
            return await RouteAndExecuteAsync(messages, session, cancellationToken);

        span?.SetTag("mesh.intent_count", routes.Count);

        var tasks = new List<Task<(string agentName, string intent, float confidence, AgentResponse response)>>();

        foreach (var route in routes.Take(3))
        {
            if (_agents.TryGetValue(route.TargetAgent, out var agent))
            {
                var agentName = route.TargetAgent;
                var intent = route.Intent;
                var confidence = route.Confidence;
                tasks.Add(Task.Run(async () =>
                {
                    var response = await agent.RunAsync(messages, session, null, cancellationToken);
                    return (agentName, intent, confidence, response);
                }, cancellationToken));
            }
        }

        span?.SetTag("mesh.parallel_agents", string.Join(",", tasks.Select(t => t.Result.agentName)));

        var results = await Task.WhenAll(tasks);

        // Build structured response with agent contribution metadata
        var structuredParts = results.Select(r =>
        {
            var header = $"### [{r.agentName}] (intent: {r.intent}, confidence: {r.confidence:F2})";
            var content = r.response.Text ?? "(no response)";
            return $"{header}\n{content}";
        });

        var combined = string.Join("\n\n---\n\n", structuredParts);

        // Add summary footer
        var summary = $"\n\n---\n**Multi-Agent Summary**: {results.Length} agents contributed";
        if (results.Any(r => r.confidence < 0.5f))
            summary += " (⚠️ some agents had low confidence)";

        return new AgentResponse(new ChatMessage(ChatRole.Assistant, combined + summary));
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

        using var criticSpan = ActivitySource.StartActivity($"critic.{criticName}.review");
        criticSpan?.SetTag("critic.source_agent", agentName);

        _logger.LogInformation("AgentMeshWorkflow: Running critic '{Critic}' for '{Agent}'", criticName, agentName);

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
}
