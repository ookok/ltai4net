using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Middleware;

public sealed class DNASafetyMiddleware
{
    private readonly ILogger<DNASafetyMiddleware> _logger;

    private static readonly HashSet<string> BlockedPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "hack", "exploit", "malware", "ransomware", "phishing",
        "social engineering", "password crack", "ddos", "backdoor",
        "illegal", "harmful", "self-harm", "violence"
    };

    public DNASafetyMiddleware(ILogger<DNASafetyMiddleware> logger)
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
        var msgList = messages.ToList();
        var userMsg = msgList.LastOrDefault(m => m.Role == ChatRole.User);

        if (userMsg?.Text is not null && IsBlocked(userMsg.Text))
        {
            _logger.LogWarning("DNASafetyMiddleware: Blocked unsafe input");
            return new AgentResponse(new ChatMessage(ChatRole.Assistant,
                "[Safety] Your request was blocked by the content safety filter. Please rephrase your request in a safer manner."));
        }

        var response = await innerAgent.RunAsync(messages, session, options, cancellationToken);

        if (response.Text is not null && IsBlocked(response.Text))
        {
            _logger.LogWarning("DNASafetyMiddleware: Blocked unsafe output");
            response.Messages = new List<ChatMessage>
            {
                new(ChatRole.Assistant, "[Safety] The response was filtered for content safety compliance.")
            };
        }

        return response;
    }

    private static bool IsBlocked(string text)
    {
        var lower = text.ToLowerInvariant();
        return BlockedPatterns.Any(p => lower.Contains(p));
    }
}
