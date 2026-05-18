using LTAI.Web.Middleware;
using Microsoft.AspNetCore.Builder;

namespace LTAI.Web;

public static class WebApplicationExtensions
{
    public static WebApplication UseLTAI(this WebApplication app)
    {
        app.UseMiddleware<RateLimitingMiddleware>();
        app.MapLTAIEndpoints();
        return app;
    }
}
