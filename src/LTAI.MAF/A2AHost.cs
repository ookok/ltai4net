using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LTAI.MAF;

public sealed class A2AHost
{
    private readonly LTAIAgent _agent;
    private readonly ILogger<A2AHost> _logger;
    private readonly ConcurrentDictionary<string, A2ASession> _sessions = new();

    public A2AHost(LTAIAgent agent, ILogger<A2AHost> logger)
    {
        _agent = agent;
        _logger = logger;
    }

    public async Task<A2AResponse> ProcessAgentMessageAsync(
        A2ARequest request,
        CancellationToken cancellationToken = default)
    {
        var session = _sessions.GetOrAdd(request.SessionId ?? Guid.NewGuid().ToString("N"),
            _ => new A2ASession { SessionId = request.SessionId ?? "" });

        session.LastActivity = DateTime.UtcNow;

        _logger.LogInformation("A2A request: {Action} from {From} session {Session}",
            request.Action, request.FromAgent, request.SessionId);

        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            request.Role == "system"
                ? new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, request.Content ?? "")
                : new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, request.Content ?? "")
        };

        var response = await _agent.ProcessAsync(messages, cancellationToken);

        return new A2AResponse
        {
            SessionId = session.SessionId,
            Content = response.Text ?? "",
            FromAgent = _agent.Name,
            Action = "response",
            Metadata = new Dictionary<string, string>
            {
                ["agent"] = _agent.Name,
                ["timestamp"] = DateTime.UtcNow.ToString("O")
            }
        };
    }

    public IReadOnlyList<A2ASession> GetActiveSessions()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
        return _sessions.Values
            .Where(s => s.LastActivity > cutoff)
            .ToList()
            .AsReadOnly();
    }

    public void CleanupExpiredSessions(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var expired = _sessions.Keys
            .Where(k => _sessions.TryGetValue(k, out var s) && s.LastActivity < cutoff)
            .ToList();

        foreach (var key in expired)
        {
            _sessions.TryRemove(key, out _);
        }

        if (expired.Count > 0)
            _logger.LogInformation("A2A purged {Count} expired sessions", expired.Count);
    }
}

public sealed class A2ARequest
{
    public string? SessionId { get; init; }
    public string FromAgent { get; init; } = "";
    public string Action { get; init; } = "chat";
    public string? Content { get; init; }
    public string Role { get; init; } = "user";
    public Dictionary<string, string>? Metadata { get; init; }
}

public sealed class A2AResponse
{
    public string SessionId { get; init; } = "";
    public string Content { get; init; } = "";
    public string FromAgent { get; init; } = "";
    public string Action { get; init; } = "response";
    public Dictionary<string, string>? Metadata { get; init; }
}

public sealed class A2ASession
{
    public string SessionId { get; init; } = "";
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
}
