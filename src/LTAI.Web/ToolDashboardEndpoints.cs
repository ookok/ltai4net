using System.Text.Json;
using LTAI.Tools.Capability.Governance;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Web;

public static class ToolDashboardEndpoints
{
    public static void MapToolDashboardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/tools/dashboard", (HttpContext context) =>
        {
            var dashboard = context.RequestServices.GetService<ToolDashboard>();
            if (dashboard == null)
                return Results.Json(new { error = "ToolDashboard not available" }, statusCode: 503);

            var report = dashboard.GetReport();
            return Results.Json(new
            {
                total_tools = report.TotalTools,
                active_tools = report.ActiveTools,
                deprecated_tools = report.DeprecatedTools,
                failing_tools = report.FailingTools,
                top_by_usage = report.TopByUsage,
                deprecated_with_replacement = report.DeprecatedWithReplacement,
                timestamp = DateTime.UtcNow
            });
        });

        endpoints.MapGet("/api/tools/dashboard/stream", async (HttpContext context, CancellationToken ct) =>
        {
            var dashboard = context.RequestServices.GetService<ToolDashboard>();
            if (dashboard == null)
            {
                context.Response.StatusCode = 503;
                return;
            }

            context.Response.ContentType = "text/event-stream";
            context.Response.Headers["Cache-Control"] = "no-cache";
            context.Response.Headers["Connection"] = "keep-alive";

            while (!ct.IsCancellationRequested)
            {
                var report = dashboard.GetReport();
                var health = dashboard.GetHealthSummary();

                var data = JsonSerializer.Serialize(new
                {
                    type = "tool_dashboard",
                    total_tools = report.TotalTools,
                    active_tools = report.ActiveTools,
                    deprecated_tools = report.DeprecatedTools,
                    failing_count = report.FailingTools.Count,
                    failing_tools = report.FailingTools.Select(f => new { f.Name, f.SuccessRate, f.Errors }),
                    top_by_usage = report.TopByUsage.Take(5).Select(t => new { t.Name, t.Invocations, t.SuccessRate }),
                    healthy_rate = health["healthy_rate"],
                    timestamp = DateTime.UtcNow
                });

                await context.Response.WriteAsync($"event: tool_dashboard\ndata: {data}\n\n", ct);
                await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);

                try { await Task.Delay(5000, ct); }
                catch (OperationCanceledException) { break; }
            }
        });

        endpoints.MapGet("/api/tools/health", (HttpContext context) =>
        {
            var dashboard = context.RequestServices.GetService<ToolDashboard>();
            if (dashboard == null)
                return Results.Json(new { error = "ToolDashboard not available" }, statusCode: 503);

            return Results.Json(dashboard.GetHealthSummary());
        });
    }
}
