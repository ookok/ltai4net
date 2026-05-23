using LTAI.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;

namespace LTAI.Web;

public static class WebApplicationExtensions
{
    public static WebApplication UseLTAI(this WebApplication app)
    {
        app.UseRateLimiter();
        app.MapLTAIEndpoints();
        app.MapAuthEndpoints();
        app.MapAuditEndpoints();
        app.MapOpenAIProxyEndpoints();
        app.MapWeWorkBotEndpoints();
        app.MapCodeApiEndpoints();
        app.MapGithubAuthEndpoints();
        app.MapSseAgentEndpoints();
        app.MapOpenCodeBridgeEndpoints();
        app.MapDocRoutesEndpoints();
        app.MapCognitionStreamEndpoints();
        app.MapWorkspaceEndpoints();
        app.MapCellGraphEndpoints();
        app.MapCoreEndpoints();
        app.MapProviderConfigApi();
        app.MapFeedbackEndpoints();
        app.MapToolDashboardEndpoints();
        app.MapHealthChecks("/api/health", new HealthCheckOptions
        {
            ResponseWriter = async (context, report) =>
            {
                context.Response.ContentType = "application/json";
                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    status = report.Status.ToString(),
                    checks = report.Entries.ToDictionary(
                        e => e.Key,
                        e => new
                        {
                            status = e.Value.Status.ToString(),
                            description = e.Value.Description,
                            duration_ms = e.Value.Duration.TotalMilliseconds
                        }),
                    timestamp = DateTime.UtcNow
                });
                await context.Response.WriteAsync(json);
            }
        });
        return app;
    }
}
