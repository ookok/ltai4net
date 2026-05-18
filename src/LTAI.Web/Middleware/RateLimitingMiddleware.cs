using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LTAI.Web.Middleware;

public sealed class RateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new();
    private readonly ILogger<RateLimitingMiddleware> _logger;
    private readonly int _maxRequestsPerMinute;

    public RateLimitingMiddleware(RequestDelegate next, ILogger<RateLimitingMiddleware> logger, int maxRequestsPerMinute = 60)
    {
        _next = next;
        _logger = logger;
        _maxRequestsPerMinute = maxRequestsPerMinute;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var bucket = _buckets.GetOrAdd(ip, _ => new TokenBucket(_maxRequestsPerMinute, _maxRequestsPerMinute));

        if (!bucket.TryConsume())
        {
            context.Response.StatusCode = 429;
            context.Response.Headers["Retry-After"] = "60";
            await context.Response.WriteAsync("Rate limit exceeded. Try again later.");
            return;
        }

        await _next(context);
    }

    private sealed class TokenBucket
    {
        private readonly double _maxTokens;
        private readonly double _refillRate;
        private double _tokens;
        private DateTime _lastRefill;

        public TokenBucket(int maxTokens, int refillPerMinute)
        {
            _maxTokens = maxTokens;
            _refillRate = refillPerMinute / 60.0;
            _tokens = maxTokens;
            _lastRefill = DateTime.UtcNow;
        }

        public bool TryConsume()
        {
            Refill();
            if (_tokens >= 1.0)
            {
                _tokens -= 1.0;
                return true;
            }
            return false;
        }

        private void Refill()
        {
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastRefill).TotalSeconds;
            _tokens = Math.Min(_maxTokens, _tokens + elapsed * _refillRate);
            _lastRefill = now;
        }
    }
}
