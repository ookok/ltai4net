using LTAI.Knowledge.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;

namespace LTAI.Web;

public static class KnowledgeGraphEndpoints
{
    public static void MapKnowledgeGraphEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/knowledge");

        // GET /api/knowledge/entities?query=&topK=10
        group.MapGet("/entities", async (HttpContext ctx, KnowledgeGraph kg) =>
        {
            var query = ctx.Request.Query["query"].FirstOrDefault() ?? "";
            var topK = int.TryParse(ctx.Request.Query["topK"].FirstOrDefault(), out var n) ? n : 10;
            var results = kg.SearchEntities(query);
            return Results.Json(new { query, count = results.Count, entities = results.Take(topK) });
        })
        .WithName("SearchKnowledgeEntities")
        .WithDescription("Search knowledge graph entities by FTS5 query");

        // GET /api/knowledge/triplets?query=&topK=10
        group.MapGet("/triplets", async (HttpContext ctx, KnowledgeGraph kg) =>
        {
            var query = ctx.Request.Query["query"].FirstOrDefault() ?? "";
            var topK = int.TryParse(ctx.Request.Query["topK"].FirstOrDefault(), out var n) ? n : 10;
            var results = kg.SearchTriplets(query);
            return Results.Json(new { query, count = results.Count, triplets = results.Take(topK) });
        })
        .WithName("SearchKnowledgeTriplets")
        .WithDescription("Search knowledge graph triplets by text match");

        // GET /api/knowledge/path?start=&end=
        group.MapGet("/path", async (HttpContext ctx, KnowledgeGraph kg) =>
        {
            var start = ctx.Request.Query["start"].FirstOrDefault() ?? "";
            var end = ctx.Request.Query["end"].FirstOrDefault() ?? "";
            if (string.IsNullOrWhiteSpace(start) || string.IsNullOrWhiteSpace(end))
                return Results.BadRequest(new { error = "start and end entity IDs are required" });
            var path = kg.FindPath(start, end);
            return Results.Json(new { start, end, found = path.Count > 0, path });
        })
        .WithName("FindKnowledgePath")
        .WithDescription("Find shortest path between two entities");

        // GET /api/knowledge/stats
        group.MapGet("/stats", async (KnowledgeGraph kg) =>
        {
            var centrality = kg.Centrality();
            return Results.Json(new
            {
                entity_count = 0, // no direct count API on KnowledgeGraph
                centrality_count = centrality.Count,
                top_centrality = centrality.OrderByDescending(c => c.InDegree + c.OutDegree).Take(10)
                    .Select(c => new { node = c.NodeId, in_degree = c.InDegree, out_degree = c.OutDegree })
            });
        })
        .WithName("KnowledgeGraphStats")
        .WithDescription("Get knowledge graph statistics");
    }
}
