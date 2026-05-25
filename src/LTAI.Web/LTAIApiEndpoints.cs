using System.Text.Json;
using LTAI.AI.Interfaces;
using LTAI.AI.Governors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace LTAI.Web;

public static class LTAIApiEndpoints
{
    public static void MapLTAIEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/chat", async (
            HttpContext context,
            ILivingTreeSystem system,
            ILogger<ILivingTreeSystem> logger,
            CancellationToken cancellationToken) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                var request = JsonSerializer.Deserialize<ChatRequest>(body);

                if (request == null || string.IsNullOrWhiteSpace(request.Query))
                {
                    context.Response.StatusCode = 400;
                    return Results.Json(new { error = "Query is required" });
                }

                logger.LogInformation("Chat request: {Query}", request.Query[..Math.Min(request.Query.Length, 200)]);

                var response = await system.ChatAsync(request.Query, cancellationToken).ConfigureAwait(false);

                return Results.Json(new ChatResponse
                {
                    Response = response,
                    Mode = system.Mode.ToString(),
                    Timestamp = DateTime.UtcNow
                });
            }
            catch (OperationCanceledException)
            {
                return Results.Json(new { error = "Request cancelled" });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Chat endpoint error");
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        endpoints.MapPost("/api/chat/stream", async (
            HttpContext context,
            ILivingTreeSystem system,
            ILogger<ILivingTreeSystem> logger,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<ChatRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.Query))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("{\"error\":\"Query required\"}", cancellationToken);
                return;
            }

            context.Response.ContentType = "text/event-stream";
            context.Response.Headers["Cache-Control"] = "no-cache";

            logger.LogInformation("SSE stream: {Query}", request.Query[..Math.Min(200, request.Query.Length)]);

            try
            {
                await foreach (var token in system.StreamChatAsync(request.Query, cancellationToken))
                {
                    var sseData = JsonSerializer.Serialize(new { text = token });
                    await context.Response.WriteAsync($"data: {sseData}\n\n", cancellationToken);
                    await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "SSE stream error");
                await context.Response.WriteAsync($"data: {{\"error\":\"{ex.Message}\"}}\n\n", cancellationToken);
            }

            await context.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
        });

        endpoints.MapGet("/api/status", async (LivingTreeSystem system) =>
        {
            object dnaInfo;
            if (system.DNAStatus != null)
            {
                var dna = system.DNAStatus;
                dnaInfo = new
                {
                    enabled = true,
                    consciousness = dna.ConsciousnessLevel.ToString(),
                    awareness = dna.AwarenessScore,
                    evolution_phase = dna.EvolutionPhase.ToString(),
                    generation = dna.Generation,
                    fitness = dna.FitnessScore,
                    safety_posture = dna.SafetyPosture.ToString(),
                    biorhythm = dna.BiorhythmPhase.ToString(),
                    energy = dna.EnergyLevel,
                    thoughts = dna.ActiveThoughts,
                    habits = dna.HabitCount
                };
            }
            else
            {
                dnaInfo = new { enabled = false };
            }

            return Results.Json(new
            {
                mode = system.Mode.ToString(),
                version = "0.51.0",
                runtime = "LTAI .NET 10",
                dna = dnaInfo
            });
        });
    }
}

public sealed record ChatRequest
{
    public string Query { get; init; } = string.Empty;
}

public sealed record ChatResponse
{
    public string Response { get; init; } = string.Empty;
    public string Mode { get; init; } = "Normal";
    public DateTime Timestamp { get; init; }
}
