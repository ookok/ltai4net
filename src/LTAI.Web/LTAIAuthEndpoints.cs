using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LTAI.Web;

public static class LTAIAuthEndpoints
{
    private static readonly ConcurrentDictionary<string, StoredUser> _users = new();
    private static byte[] _jwtSecret = Array.Empty<byte>();
    private static readonly object _secretLock = new();

    private sealed class StoredUser
    {
        public string Id { get; init; } = string.Empty;
        public string Username { get; init; } = string.Empty;
        public string PasswordHash { get; init; } = string.Empty;
        public string Role { get; init; } = string.Empty;
        public string Email { get; init; } = string.Empty;
    }

    static LTAIAuthEndpoints()
    {
        _jwtSecret = LoadOrCreateSecret();
        var adminHash = HashPassword("admin123", _jwtSecret);
        _users.TryAdd("admin", new StoredUser
        {
            Id = "1",
            Username = "admin",
            PasswordHash = adminHash,
            Role = "admin",
            Email = "admin@ltai.local"
        });
    }

    public static void MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/auth/login", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync().ConfigureAwait(false);
                var request = JsonSerializer.Deserialize<LoginRequest>(body);

                if (request == null || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Username and password are required" }));
                    return;
                }

                if (!_users.TryGetValue(request.Username, out var user))
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Invalid credentials" }));
                    return;
                }

                var passwordHash = HashPassword(request.Password, _jwtSecret);
                if (!string.Equals(passwordHash, user.PasswordHash, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Invalid credentials" }));
                    return;
                }

                var expiry = DateTimeOffset.UtcNow.AddHours(1);
                var token = CreateToken(user.Id, user.Role, expiry);

                var response = new LoginResponse(
                    Token: token,
                    ExpiresAt: expiry,
                    User: new UserInfo(Id: user.Id, Username: user.Username, Role: user.Role, Email: user.Email)
                );

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message })).ConfigureAwait(false);
            }
        });

        endpoints.MapPost("/api/auth/login/wework", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync().ConfigureAwait(false);
                var request = JsonSerializer.Deserialize<WeWorkLoginRequest>(body);

                if (request == null || string.IsNullOrWhiteSpace(request.Code))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Code is required" }));
                    return;
                }

                var expiry = DateTimeOffset.UtcNow.AddHours(1);
                var token = CreateToken("wework_user", "user", expiry);

                var response = new LoginResponse(
                    Token: token,
                    ExpiresAt: expiry,
                    User: new UserInfo(Id: "wework_user", Username: "wework_user", Role: "user", Email: "wework@ltai.local")
                );

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message })).ConfigureAwait(false);
            }
        });

        endpoints.MapGet("/api/auth/me", async (HttpContext context) =>
        {
            try
            {
                var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Authorization header required" }));
                    return;
                }

                var token = authHeader["Bearer ".Length..].Trim();
                var result = ValidateToken(token);
                if (result == null)
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Invalid or expired token" }));
                    return;
                }

                var (userId, role) = result.Value;
                var user = _users.Values.FirstOrDefault(u => u.Id == userId);

                var userInfo = new UserInfo(
                    Id: userId,
                    Username: user?.Username ?? "unknown",
                    Role: role,
                    Email: user?.Email ?? ""
                );

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(userInfo)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message })).ConfigureAwait(false);
            }
        });

        endpoints.MapPost("/api/auth/refresh", async (HttpContext context) =>
        {
            try
            {
                var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Authorization header required" }));
                    return;
                }

                var token = authHeader["Bearer ".Length..].Trim();
                var result = ValidateToken(token);
                if (result == null)
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Invalid or expired token" }));
                    return;
                }

                var (userId, role) = result.Value;
                var expiry = DateTimeOffset.UtcNow.AddHours(1);
                var newToken = CreateToken(userId, role, expiry);

                var response = new LoginResponse(
                    Token: newToken,
                    ExpiresAt: expiry,
                    User: new UserInfo(Id: userId, Username: "", Role: role, Email: "")
                );

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message })).ConfigureAwait(false);
            }
        });

        endpoints.MapGet("/api/auth/config", async (HttpContext context) =>
        {
            var config = new AuthConfig(
                WeworkEnabled: false,
                GithubEnabled: false
            );

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(config)).ConfigureAwait(false);
        });
    }

    private static string CreateToken(string userId, string role, DateTimeOffset expiry)
    {
        var header = new { alg = "HS256", typ = "JWT" };
        var payload = new
        {
            sub = userId,
            role,
            exp = expiry.ToUnixTimeSeconds(),
            iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            iss = "ltai"
        };

        var headerJson = JsonSerializer.Serialize(header);
        var payloadJson = JsonSerializer.Serialize(payload);

        var headerB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var payloadB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));

        var signingInput = $"{headerB64}.{payloadB64}";
        var signature = HMACSHA256.HashData(_jwtSecret, Encoding.UTF8.GetBytes(signingInput));
        var signatureB64 = Base64UrlEncode(signature);

        return $"{headerB64}.{payloadB64}.{signatureB64}";
    }

    private static (string userId, string role)? ValidateToken(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3)
                return null;

            var headerB64 = parts[0];
            var payloadB64 = parts[1];
            var signatureB64 = parts[2];

            var signingInput = $"{headerB64}.{payloadB64}";
            var expectedSignature = HMACSHA256.HashData(_jwtSecret, Encoding.UTF8.GetBytes(signingInput));
            var expectedSignatureB64 = Base64UrlEncode(expectedSignature);

            if (!string.Equals(signatureB64, expectedSignatureB64, StringComparison.Ordinal))
                return null;

            var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(payloadB64));
            using var doc = JsonDocument.Parse(payloadJson);

            var exp = doc.RootElement.GetProperty("exp").GetInt64();
            var expiry = DateTimeOffset.FromUnixTimeSeconds(exp);
            if (expiry < DateTimeOffset.UtcNow)
                return null;

            var userId = doc.RootElement.GetProperty("sub").GetString() ?? "";
            var role = doc.RootElement.GetProperty("role").GetString() ?? "";

            return (userId, role);
        }
        catch
        {
            return null;
        }
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var base64 = input.Replace('-', '+').Replace('_', '/');
        switch (base64.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }
        return Convert.FromBase64String(base64);
    }

    private static string HashPassword(string password, byte[] secret)
    {
        var hash = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(password));
        return Convert.ToBase64String(hash);
    }

    private static byte[] LoadOrCreateSecret()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dir);
        var keyPath = Path.Combine(dir, "jwt_secret.key");

        if (File.Exists(keyPath))
            return File.ReadAllBytes(keyPath);

        var secret = new byte[32];
        RandomNumberGenerator.Fill(secret);
        File.WriteAllBytes(keyPath, secret);
        return secret;
    }
}

public sealed record LoginRequest(string Username, string Password);
public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt, UserInfo User);
public sealed record UserInfo(string Id, string Username, string Role, string Email);
public sealed record WeWorkLoginRequest(string Code);
public sealed record AuthConfig(bool WeworkEnabled, bool GithubEnabled);
