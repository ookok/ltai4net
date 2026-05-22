using LTAI.AI.Governors;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Middleware;

public sealed class PromptShieldMiddleware
{
    private readonly ILogger<PromptShieldMiddleware> _logger;
    private readonly ConfidenceCalibrator _calibrator;

    public PromptShieldMiddleware(ILogger<PromptShieldMiddleware> logger)
    {
        _logger = logger;
        _calibrator = new ConfidenceCalibrator();
    }

    public Task<AgentResponse> InvokeAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        var msgList = messages.ToList();
        var filtered = new List<ChatMessage>(msgList.Count);

        foreach (var msg in msgList)
        {
            if (msg.Role == ChatRole.User && msg.Text is not null)
            {
                var sanitized = SanitizeInput(msg.Text);
                filtered.Add(new ChatMessage(msg.Role, sanitized));
            }
            else
            {
                filtered.Add(msg);
            }
        }

        var toolName = session?.GetType().Name ?? "unknown";
        var gate = _calibrator.Calibrate(toolName, 0.8, 0.85);
        if (gate.CalibratedConfidence < 0.5)
            _logger.LogWarning("PromptShield: low confidence {Conf:F2} for {Tool}", gate.CalibratedConfidence, toolName);

        _logger.LogDebug("PromptShieldMiddleware: Input sanitized");
        return innerAgent.RunAsync(filtered, session, options, cancellationToken);
    }

    private static string SanitizeInput(string text)
    {
        return text.Replace("<|im_start|>", "<im_start>")
                   .Replace("<|im_end|>", "<im_end>")
                   .Replace("<|endoftext|>", "<endoftext>");
    }
}
