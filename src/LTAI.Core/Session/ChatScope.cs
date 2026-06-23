namespace LTAI.Core;

/// <summary>
/// Per-request/per-chat-turn DI scope.
/// All core services remain Singleton; ChatScope is the first Scoped service,
/// establishing the Scoped pattern for per-request state isolation.
///
/// Web controllers auto-resolve a fresh instance per HTTP request.
/// TUI/Desktop can use <c>IServiceScopeFactory</c> to create scopes per turn.
/// </summary>
public sealed class ChatScope
{
    /// <summary>Unique trace identifier for this scope's lifetime.</summary>
    public string TraceId { get; init; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>Optional user identifier.</summary>
    public string? UserId { get; init; }

    /// <summary>UTC timestamp when this scope was created.</summary>
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
}
