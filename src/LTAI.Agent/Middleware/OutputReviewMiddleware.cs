using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Middleware;

public sealed class OutputReviewMiddleware
{
    private readonly ILogger<OutputReviewMiddleware> _logger;

    public OutputReviewMiddleware(ILogger<OutputReviewMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task<AgentResponse> InvokeAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        var response = await innerAgent.RunAsync(messages, session, options, cancellationToken);

        if (response.Text is not null)
        {
            var reviewed = ReviewOutput(response.Text);
            if (reviewed != response.Text)
            {
                _logger.LogDebug("OutputReviewMiddleware: Output reviewed and modified");
                response.Messages = new List<ChatMessage>
                {
                    new(ChatRole.Assistant, reviewed)
                };
            }
        }

        _logger.LogDebug("OutputReviewMiddleware: Output passed review");
        return response;
    }

    private static string ReviewOutput(string text)
    {
        if (text.Contains("<script", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            return text.Replace("<script", "&lt;script", StringComparison.OrdinalIgnoreCase)
                       .Replace("javascript:", "blocked:", StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }
}
