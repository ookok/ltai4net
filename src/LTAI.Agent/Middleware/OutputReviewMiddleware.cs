using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Middleware;

public sealed class OutputReviewMiddleware
{
    private readonly ILogger<OutputReviewMiddleware> _logger;

    private static readonly (Regex Pattern, string Replacement, string Category)[] OutputRules =
    [
        (new(@"<script", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "&lt;script", "XSS"),
        (new(@"javascript:", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "blocked:", "XSS"),
        (new(@"on(error|load|click|mouseover|focus|blur)\s*=", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "on$1_blocked=", "XSS"),
        (new(@"\b(DROP|ALTER|TRUNCATE)\s+(TABLE|DATABASE|SCHEMA)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "[SQL filtered]", "SQL-Injection"),
        (new(@"\b(DELETE|INSERT|UPDATE)\s+(FROM|INTO)\s+\w+", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "[SQL filtered]", "SQL-Injection"),
        (new(@";\s*(DROP|DELETE|INSERT|UPDATE|ALTER)\s", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "; [SQL filtered] ", "SQL-Injection"),
        (new(@"(\.\./){2,}"),
            "[path-traversal filtered]", "Path-Traversal"),
        (new(@"\\(\.\.\\){2,}"),
            "[path-traversal filtered]", "Path-Traversal"),
        (new(@"\b(rm\s+(-\w+\s+)*-[a-zA-Z]*r[a-zA-Z]*f[a-zA-Z]*\s+/)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "[command filtered]", "Command-Injection"),
        (new(@"\b(format\s+[a-zA-Z]:)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "[command filtered]", "Command-Injection"),
        (new(@"\b(del\s+/[fFsS]\s+)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "[command filtered]", "Command-Injection"),
        (new(@"(curl|wget)\s+\S+\s*\|\s*(bash|sh|pwsh|zsh)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "[pipe-to-shell filtered]", "Command-Injection"),
        (new("(api[_-]?key|password|passwd|secret|token|access[_-]?key|private[_-]?key)\\s*[:=]\\s*['\"][^'\"]{8,}['\"]", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "[credential redacted]", "Credential-Leak"),
        (new(@"-----BEGIN\s+(RSA\s+)?PRIVATE\s+KEY-----"),
            "[private-key redacted]", "Credential-Leak"),
        (new(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b", RegexOptions.Compiled),
            "[email redacted]", "PII-Leak"),
    ];

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
            var (reviewed, categories) = ReviewOutput(response.Text);
            if (reviewed != response.Text)
            {
                _logger.LogWarning("OutputReviewMiddleware: Output modified, categories={Categories}",
                    string.Join(",", categories));
                response.Messages = new List<ChatMessage>
                {
                    new(ChatRole.Assistant, reviewed)
                };
            }
        }

        _logger.LogDebug("OutputReviewMiddleware: Output passed review");
        return response;
    }

    private static (string text, HashSet<string> categories) ReviewOutput(string text)
    {
        var result = text;
        var categories = new HashSet<string>();

        foreach (var (pattern, replacement, category) in OutputRules)
        {
            if (pattern.IsMatch(result))
            {
                result = pattern.Replace(result, replacement);
                categories.Add(category);
            }
        }

        return (result, categories);
    }
}
