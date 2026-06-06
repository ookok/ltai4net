using System.Collections.Concurrent;

namespace LTAI.Web.Middleware;

/// <summary>
/// Fixed-window rate limiter per IP address.
/// Configuration via env vars:
///   LTAI_RATE_LIMIT_REQUESTS — max requests per window (default: 60)
///   LTAI_RATE_LIMIT_WINDOW_SEC — window in seconds (default: 60)
/// </summary>
public sealed class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly int _maxRequests;
    private readonly int _windowSec;
    private readonly ConcurrentDictionary<string, WindowState> _windows = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);
    private DateTime _lastCleanup = DateTime.UtcNow;

    public RateLimitMiddleware(RequestDelegate next)
    {
        _next = next;
        _maxRequests = int.TryParse(Environment.GetEnvironmentVariable("LTAI_RATE_LIMIT_REQUESTS"), out var r) ? r : 60;
        _windowSec = int.TryParse(Environment.GetEnvironmentVariable("LTAI_RATE_LIMIT_WINDOW_SEC"), out var w) ? w : 60;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip rate limiting for health checks
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Respect X-Forwarded-For when behind reverse proxy
        var ip = context.Request.Headers["X-Forwarded-For"].FirstOrDefault()
                  ?? context.Connection.RemoteIpAddress?.ToString()
                  ?? "unknown";
        // Take the first IP in chain (original client)
        if (ip.Contains(',')) ip = ip.Split(',')[0].Trim();
        var now = DateTime.UtcNow;

        // Periodic cleanup of stale entries
        if (now - _lastCleanup > CleanupInterval)
        {
            _lastCleanup = now;
            foreach (var kv in _windows)
            {
                if (now - kv.Value.WindowStart > TimeSpan.FromSeconds(_windowSec * 2))
                    _windows.TryRemove(kv.Key, out _);
            }
        }

        var window = _windows.AddOrUpdate(ip,
            _ => new WindowState(1, now),
            (_, state) => now - state.WindowStart > TimeSpan.FromSeconds(_windowSec)
                ? new WindowState(1, now)
                : state with { Count = state.Count + 1 });

        context.Response.Headers["X-RateLimit-Limit"] = _maxRequests.ToString();
        context.Response.Headers["X-RateLimit-Remaining"] = Math.Max(0, _maxRequests - window.Count).ToString();

        if (window.Count > _maxRequests)
        {
            var retryAfter = (int)(_windowSec - (now - window.WindowStart).TotalSeconds) + 1;
            context.Response.StatusCode = 429;
            context.Response.Headers["Retry-After"] = retryAfter.ToString();
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                "{\"error\":\"Rate limit exceeded\",\"type\":\"rate_limited\",\"retry_after_seconds\":" + retryAfter + "}").ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    private sealed record WindowState(long Count, DateTime WindowStart);
}
