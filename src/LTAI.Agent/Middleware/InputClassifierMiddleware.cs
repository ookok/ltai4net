using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Middleware;

public sealed class InputClassifierMiddleware
{
    private readonly ILogger<InputClassifierMiddleware> _logger;

    public InputClassifierMiddleware(ILogger<InputClassifierMiddleware> logger)
    {
        _logger = logger;
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
            var intent = ClassifyIntent(userMsg.Text);
            _logger.LogDebug("InputClassifierMiddleware: Intent={Intent}", intent);
        }

        return innerAgent.RunAsync(messages, session, options, cancellationToken);
    }

    private static string ClassifyIntent(string text)
    {
        var lower = text.ToLowerInvariant();

        if (lower.Contains("code") || lower.Contains("class ") || lower.Contains("function ") || lower.Contains("bug") || lower.Contains("error") || lower.Contains("build"))
            return "code";

        if (lower.Contains("environment") || lower.Contains("impact") || lower.Contains("emission") || lower.Contains("gis") || lower.Contains("map") || lower.Contains("spatial"))
            return "eia";

        if (lower.Contains("why") || lower.Contains("explain") || lower.Contains("reason") || lower.Contains("compare") || lower.Contains("analyze") || lower.Contains("think"))
            return "reasoning";

        return "chat";
    }
}
