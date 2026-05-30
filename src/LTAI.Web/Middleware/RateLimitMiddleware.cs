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
    private readonly ConcurrentDictionary<string, WindowState> _windows = new();

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
            await _next(context);
            return;
        }

        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var now = DateTime.UtcNow;
        var window = _windows.AddOrUpdate(ip, _ =>
        {
            return new WindowState { Count = 1, WindowStart = now };
        }, (_, state) =>
        {
            if (now - state.WindowStart > TimeSpan.FromSeconds(_windowSec))
                return new WindowState { Count = 1, WindowStart = now };
            state.Count++;
            return state;
        });

        context.Response.Headers["X-RateLimit-Limit"] = _maxRequests.ToString();
        context.Response.Headers["X-RateLimit-Remaining"] = Math.Max(0, _maxRequests - window.Count).ToString();

        if (window.Count > _maxRequests)
        {
            var retryAfter = (int)(_windowSec - (now - window.WindowStart).TotalSeconds) + 1;
            context.Response.StatusCode = 429;
            context.Response.Headers["Retry-After"] = retryAfter.ToString();
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                "{\"error\":\"Rate limit exceeded\",\"type\":\"rate_limited\",\"retry_after_seconds\":" + retryAfter + "}");
            return;
        }

        await _next(context);
    }

    private sealed class WindowState
    {
        public int Count;
        public DateTime WindowStart;
    }
}
