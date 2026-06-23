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
    private readonly byte[]? _configuredKeyBytes;    // cached for constant-time comparison
    private readonly IConfiguration? _config;

    public ApiKeyMiddleware(RequestDelegate next, IConfiguration? config = null)
    {
        _next = next;
        _config = config;
        _configuredKey = LTAI.Core.Configuration.EnvironmentConfig.WebApiKey
                      ?? config?["LTAI:ApiKey"];
        _configuredKeyBytes = _configuredKey != null
            ? Encoding.UTF8.GetBytes(_configuredKey)
            : null;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip auth for health checks and swagger
        if (context.Request.Path.StartsWithSegments("/health") ||
            context.Request.Path.StartsWithSegments("/ready") ||
            context.Request.Path.StartsWithSegments("/swagger"))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // No key configured → require explicit dev-mode opt-in
        if (string.IsNullOrEmpty(_configuredKey))
        {
            var allowDev = LTAI.Core.Configuration.EnvironmentConfig.DevMode
                || string.Equals(_config?["LTAI:DevMode"], "true", StringComparison.OrdinalIgnoreCase);
            if (!allowDev)
            {
                context.Response.StatusCode = 401;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    "{\"error\":\"Authentication required\",\"type\":\"auth_required\",\"hint\":\"Set LTAI_API_KEY env var or LTAI_DEV_MODE=true for development\"}").ConfigureAwait(false);
                return;
            }
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Try HMAC-SHA256 signature verification
        // Signature includes method + path + timestamp + nonce to prevent replay attacks
        // Requires X-Timestamp (Unix seconds) and X-Nonce headers
        var signature = context.Request.Headers["X-Signature"].FirstOrDefault();
        var timestampStr = context.Request.Headers["X-Timestamp"].FirstOrDefault();
        var nonce = context.Request.Headers["X-Nonce"].FirstOrDefault();
        if (!string.IsNullOrEmpty(signature) && !string.IsNullOrEmpty(timestampStr) && !string.IsNullOrEmpty(nonce))
        {
            if (long.TryParse(timestampStr, out var ts) && Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts) <= 30)
            {
                var method = context.Request.Method;
                var path = context.Request.Path.ToString();
                var message = $"{method}|{path}|{timestampStr}|{nonce}";
                var expectedHmac = ComputeHmac(_configuredKey, message);
                if (ConstantTimeEquals(expectedHmac, signature))
                {
                    await _next(context).ConfigureAwait(false);
                    return;
                }
            }
        }

        // Fall back to API key header verification
        var provided = context.Request.Headers["X-API-Key"].FirstOrDefault()
                    ?? ExtractBearer(context);

        if (string.IsNullOrEmpty(provided) || !ConstantTimeEqualsCached(provided))
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

    private bool ConstantTimeEqualsCached(string provided)
    {
        if (_configuredKeyBytes == null) return false;
        if (provided.Length != _configuredKey.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), _configuredKeyBytes);
    }
}
