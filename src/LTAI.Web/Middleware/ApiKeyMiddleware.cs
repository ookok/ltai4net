using System.Security.Cryptography;
using System.Text;

namespace LTAI.Web.Middleware;

/// <summary>
/// API Key authentication middleware.
/// Two modes:
///   - If ApiKey env var is not set → allow all (dev mode)
///   - If ApiKey env var is set → require X-API-Key header or Authorization: Bearer <key>
/// </summary>
public sealed class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string? _configuredKey;

    public ApiKeyMiddleware(RequestDelegate next, IConfiguration? config = null)
    {
        _next = next;
        // Read API_KEY from env or config (but never from query string)
        _configuredKey = Environment.GetEnvironmentVariable("LTAI_API_KEY")
                      ?? config?["LTAI:ApiKey"];
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip auth for health check and swagger
        if (context.Request.Path.StartsWithSegments("/health") ||
            context.Request.Path.StartsWithSegments("/swagger"))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // No key configured → dev mode, allow all
        if (string.IsNullOrEmpty(_configuredKey))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Try HMAC-SHA256 signature verification first
        // Signature format: HMAC-SHA256 hex-encoded HMAC of the request path
        var signature = context.Request.Headers["X-Signature"].FirstOrDefault();
        if (!string.IsNullOrEmpty(signature))
        {
            var path = context.Request.Path.ToString();
            var expectedHmac = ComputeHmac(_configuredKey, path);
            if (ConstantTimeEquals(expectedHmac, signature))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }
        }

        // Fall back to API key header verification
        var provided = context.Request.Headers["X-API-Key"].FirstOrDefault()
                    ?? ExtractBearer(context);

        if (string.IsNullOrEmpty(provided) || !ConstantTimeEquals(_configuredKey, provided))
        {
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                "{\"error\":\"Unauthorized\",\"type\":\"auth_required\"}").ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    /// <summary>Compute HMAC-SHA256 hex string for a given key and message.</summary>
    private static string ComputeHmac(string key, string message)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var msgBytes = Encoding.UTF8.GetBytes(message);
        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(msgBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? ExtractBearer(HttpContext context)
    {
        var auth = context.Request.Headers.Authorization.FirstOrDefault();
        if (auth == null || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;
        return auth["Bearer ".Length..].Trim();
    }

    private static bool ConstantTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    }
}
