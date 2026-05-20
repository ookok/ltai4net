using LTAI.Core.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;

namespace LTAI.MAF;

public static class A2AAuthExtensions
{
    private const string BearerHeader = "Authorization";

    public static IEndpointConventionBuilder RequireLTAIAuth(this IEndpointConventionBuilder builder)
    {
        return builder.RequireAuthorization();
    }

    public static void UseA2ABearerAuth(this WebApplication app, string? a2aToken = null)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/a2a"))
            {
                var token = a2aToken ?? SecretVault.Instance.Get("a2a_bearer_token");
                if (!string.IsNullOrWhiteSpace(token))
                {
                    var authHeader = context.Request.Headers[BearerHeader].FirstOrDefault();
                    if (authHeader == null || !authHeader.StartsWith("Bearer ") ||
                        !authHeader["Bearer ".Length..].Equals(token, StringComparison.Ordinal))
                    {
                        context.Response.StatusCode = 401;
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync("""{"error":"Unauthorized","detail":"Valid Bearer token required"}""");
                        return;
                    }
                }
            }
            await next();
        });
    }
}
