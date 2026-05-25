using LTAI.AI.Governors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Web;

public static class CellGraphEndpoints
{
    public static void MapCellGraphEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api")
            .WithTags("Cell & Graph Management")
            .RequireRateLimiting("fixed");

        // ==================== 细胞管理 API ====================

        group.MapGet("/cells/status", async (CascadeLoader loader, DomainDiscoveryService discovery) =>
        {
            var stats = loader.GetStats();
            var statuses = loader.GetLoadStatuses();
            var nursery = discovery.GetNurseryStats();

            return Results.Ok(new
            {
                stats,
                statuses,
                nursery
            });
        })
        .WithName("GetCellStatus")
;

        group.MapPost("/cells/{domain}/load", async (
            string domain,
            CascadeLoader loader,
            [FromQuery] int priority = 0) =>
        {
            var status = await loader.LoadCellCascadeAsync($"cell-{domain}", domain, priority);
            if (status == null || status.State == CellLoadState.Failed)
            {
                return Results.BadRequest(new { error = "Failed to load cell", details = status?.Error });
            }
            return Results.Ok(status);
        })
        .WithName("LoadCell")
;

        group.MapPost("/cells/{domain}/unload", async (
            string domain,
            CascadeLoader loader) =>
        {
            var success = await loader.UnloadCellAsync($"cell-{domain}");
            return success ? Results.Ok(new { message = "Cell unloaded" }) : Results.NotFound();
        })
        .WithName("UnloadCell")
;

        group.MapGet("/cells/search", async (
            [FromQuery] string? domain,
            [FromQuery] string? tag,
            GitHubCellRegistry registry) =>
        {
            var results = await registry.SearchCellsAsync(domain, tag).ConfigureAwait(false);
            return Results.Ok(results);
        })
        .WithName("SearchCells")
;

        group.MapPost("/cells/{id}/download", async (
            string id,
            GitHubCellRegistry registry,
            [FromQuery] string version = "latest") =>
        {
            var package = await registry.DownloadCellAsync(id, version).ConfigureAwait(false);
            if (package == null)
            {
                return Results.NotFound(new { error = "Cell not found or download failed" });
            }
            return Results.Ok(package);
        })
        .WithName("DownloadCell")
;

        // ==================== 图谱管理 API ====================

        group.MapGet("/graphs/status", async (GraphCascadeLoader loader, DomainGraphRegistry registry) =>
        {
            var stats = loader.GetStats();
            var statuses = loader.GetLoadStatuses();
            var registryStats = registry.GetStats();

            return Results.Ok(new
            {
                stats,
                statuses,
                registryStats
            });
        })
        .WithName("GetGraphStatus")
;

        group.MapPost("/graphs/{domain}/load", async (
            string domain,
            GraphCascadeLoader loader,
            [FromQuery] int priority = 0) =>
        {
            var status = await loader.LoadGraphCascadeAsync($"graph-{domain}", domain, priority);
            if (status == null || status.State == GraphLoadState.Failed)
            {
                return Results.BadRequest(new { error = "Failed to load graph", details = status?.Error });
            }
            return Results.Ok(status);
        })
        .WithName("LoadGraph")
;

        group.MapGet("/graphs/search", async (
            [FromQuery] string? domain,
            [FromQuery] string? tag,
            GitHubGraphRegistry registry) =>
        {
            var results = await registry.SearchGraphsAsync(domain, tag).ConfigureAwait(false);
            return Results.Ok(results);
        })
        .WithName("SearchGraphs")
;

        group.MapPost("/graphs/{id}/download", async (
            string id,
            GitHubGraphRegistry registry,
            [FromQuery] string version = "latest") =>
        {
            var package = await registry.DownloadGraphAsync(id, version).ConfigureAwait(false);
            if (package == null)
            {
                return Results.NotFound(new { error = "Graph not found or download failed" });
            }
            return Results.Ok(package);
        })
        .WithName("DownloadGraph")
;

        // ==================== 系统状态 API ====================

        group.MapGet("/system/status", async (
            CellAIRegistry cellRegistry,
            DomainGraphRegistry graphRegistry,
            DomainDiscoveryService discovery) =>
        {
            var cellMetrics = cellRegistry.GetMetrics();
            var graphStats = graphRegistry.GetStats();
            var nursery = discovery.GetNurseryStats();

            return Results.Ok(new
            {
                cells = cellMetrics,
                graphs = graphStats,
                nursery,
                timestamp = DateTime.UtcNow
            });
        })
        .WithName("GetSystemStatus")
;
    }
}
