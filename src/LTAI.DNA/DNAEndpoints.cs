using System.Text.Json;
using LTAI.DNA.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LTAI.DNA;

public static class DNAEndpoints
{
    public static void MapDNAEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Core status
        endpoints.MapGet("/api/dna/status", (DNAOrchestrator dna) =>
            Results.Json(dna.GetStatus()));

        // Safety (retained)
        endpoints.MapGet("/api/dna/safety", (DNAOrchestrator dna) =>
            Results.Json(dna.Safety.GetStatus()));

        endpoints.MapPost("/api/dna/safety/posture", async (
            HttpContext context, DNAOrchestrator dna, CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<Dictionary<string, string>>(body);
            if (request != null && request.TryGetValue("posture", out var postureStr) &&
                Enum.TryParse<SafetyPosture>(postureStr, true, out var posture))
            {
                dna.Safety.SetPosture(posture);
                return Results.Json(new { posture = posture.ToString() });
            }
            return Results.Json(new { error = "Invalid posture" }, statusCode: 400);
        });

        // World model (retained)
        endpoints.MapGet("/api/dna/world", (DNAOrchestrator dna) =>
            Results.Json(new { entities = dna.World.EntityCount, relations = dna.World.RelationCount, accuracy = dna.World.Accuracy }));

        // Predictive engine (retained)
        endpoints.MapGet("/api/dna/predict", (DNAOrchestrator dna, string metric) =>
        {
            var forecast = dna.Predictor.Forecast(metric);
            var trending = dna.Predictor.GetTrending(3);
            return Results.Json(new { metric, forecast, trending });
        });

        // Mental Time Travel memory (retained)
        endpoints.MapGet("/api/dna/memory", (DNAOrchestrator dna, string? query) =>
        {
            var recall = query != null ? dna.MTT.Recall(query) : $"Episodes: {dna.MTT.EpisodeCount}";
            return Results.Json(new { recall });
        });

        // RLVR monitor (retained)
        endpoints.MapGet("/api/dna/rlvr", (DNAOrchestrator dna) =>
            Results.Json(dna.RLVR.Stats()));

        // Narrative
        endpoints.MapGet("/api/dna/narrative", (DNAOrchestrator dna) =>
            Results.Json(new { narrative = dna.GenerateSelfNarrative() }));
    }
}
