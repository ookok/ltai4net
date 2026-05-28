using System.Diagnostics;

namespace LTAI.Core.System;

/// <summary>
/// Provides a unified trace identifier that flows across async calls via AsyncLocal.
/// This connects MicroKernel audit entries, ParetoRouter decisions, ArchitectLoop proposals,
/// and PartStreamStore sessions under a single correlation ID.
/// </summary>
public interface ITraceContext
{
    /// <summary>Current trace ID — 32-char hex (16 bytes). Generated once per root request.</summary>
    string TraceId { get; }

    /// <summary>Parent trace ID for nested/cascaded operations.</summary>
    string? ParentId { get; }

    /// <summary>Unix timestamp milliseconds when the trace was started.</summary>
    long StartedAt { get; }

    /// <summary>Begin a new trace scope. Returns the new TraceId. If already in a trace, nests as parent.</summary>
    string Begin(string? operation = null);

    /// <summary>End the current trace scope, restoring the parent if any.</summary>
    void End();
}

/// <summary>
/// AsyncLocal-based implementation of ITraceContext.
/// Register as Scoped in DI to get per-request isolation.
/// </summary>
public sealed class TraceContext : ITraceContext
{
    private static readonly AsyncLocal<TraceScope?> CurrentScope = new();

    private sealed class TraceScope
    {
        public string TraceId { get; }
        public string? ParentId { get; }
        public long StartedAt { get; }
        public TraceScope? Previous { get; }

        public TraceScope(string traceId, string? parentId, TraceScope? previous)
        {
            TraceId = traceId;
            ParentId = parentId;
            StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Previous = previous;
        }
    }

    public string TraceId => CurrentScope.Value?.TraceId ?? "00000000000000000000000000000000";
    public string? ParentId => CurrentScope.Value?.ParentId;
    public long StartedAt => CurrentScope.Value?.StartedAt ?? 0;

    public string Begin(string? operation = null)
    {
        var newId = GenerateTraceId();
        var scope = new TraceScope(newId, CurrentScope.Value?.TraceId, CurrentScope.Value);
        CurrentScope.Value = scope;

        if (operation != null)
        {
            // Tag the active Activity if OpenTelemetry is in use
            Activity.Current?.SetTag("ltai.trace_id", newId);
            Activity.Current?.SetTag("ltai.operation", operation);
        }

        return newId;
    }

    public void End()
    {
        var current = CurrentScope.Value;
        if (current != null)
            CurrentScope.Value = current.Previous;
    }

    /// <summary>
    /// Generate a 32-char hex trace ID (16 random bytes).
    /// </summary>
    public static string GenerateTraceId()
    {
        Span<byte> bytes = stackalloc byte[16];
        global::System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }
}

/// <summary>
/// Extension methods for convenience DI registration.
/// </summary>
public static class TraceContextExtensions
{
    /// <summary>
    /// Create a trace scope, execute the function, and end the scope.
    /// </summary>
    public static T ExecuteInScope<T>(this ITraceContext ctx, string operation, Func<string, T> func)
    {
        var traceId = ctx.Begin(operation);
        try
        {
            return func(traceId);
        }
        finally
        {
            ctx.End();
        }
    }

    /// <summary>
    /// Create a trace scope, execute the async function, and end the scope.
    /// </summary>
    public static async Task<T> ExecuteInScopeAsync<T>(this ITraceContext ctx, string operation, Func<string, Task<T>> func)
    {
        var traceId = ctx.Begin(operation);
        try
        {
            return await func(traceId).ConfigureAwait(false);
        }
        finally
        {
            ctx.End();
        }
    }
}
