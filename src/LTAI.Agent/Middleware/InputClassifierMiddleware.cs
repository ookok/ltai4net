using LTAI.Agent.Routing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Middleware;

public sealed class InputClassifierMiddleware
{
    private readonly ILogger<InputClassifierMiddleware> _logger;
    private readonly UnifiedIntentRouter _router;

    public InputClassifierMiddleware(ILogger<InputClassifierMiddleware> logger, UnifiedIntentRouter router)
    {
        _logger = logger;
        _router = router;
    }

    public Task<AgentResponse> InvokeAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        var msgList = messages.ToList();
        var userMsg = msgList.LastOrDefault(m => m.Role == ChatRole.User);

        if (userMsg?.Text is not null)
        {
            var route = _router.Route(userMsg.Text);
            _logger.LogDebug("InputClassifierMiddleware: Intent={Intent} Shape={Shape} Workflow={Workflow}",
                route.Intent, route.QueryShape ?? "none", route.UseWorkflow);

            options ??= new AgentRunOptions();
            options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            options.AdditionalProperties["classified_intent"] = route.Intent;
            options.AdditionalProperties["query_shape"] = route.QueryShape;
            options.AdditionalProperties["use_workflow"] = route.UseWorkflow;
            options.AdditionalProperties["target_agent"] = route.TargetAgent;
        }

        return innerAgent.RunAsync(messages, session, options, cancellationToken);
    }
}
