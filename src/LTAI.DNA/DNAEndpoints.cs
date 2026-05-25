using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LTAI.DNA;

public static class DNAEndpoints
{
    public static void MapDNAEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/dna/status", (DNAOrchestrator dna) =>
        {
            var status = dna.GetStatus();
            return Results.Json(status);
        });

        endpoints.MapGet("/api/dna/consciousness", (DNAOrchestrator dna) =>
        {
            var c = dna.Consciousness.State;
            return Results.Json(new
            {
                level = c.Level.ToString(),
                awareness = c.AwarenessScore,
                self_model = c.SelfModelAccuracy,
                world_model = c.WorldModelAccuracy,
                active_thoughts = c.ActiveThoughts,
                attention = c.AttentionVector.OrderByDescending(kvp => kvp.Value).Take(10)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
            });
        });

        endpoints.MapGet("/api/dna/life", (DNAOrchestrator dna) =>
        {
            var l = dna.Life;
            return Results.Json(new
            {
                personality = new
                {
                    openness = l.Personality.Openness,
                    conscientiousness = l.Personality.Conscientiousness,
                    extraversion = l.Personality.Extraversion,
                    agreeableness = l.Personality.Agreeableness,
                    neuroticism = l.Personality.Neuroticism,
                    curiosity = l.Personality.CuriosityDrive,
                    style = l.Personality.CommunicationStyle
                },
                biorhythm = new
                {
                    phase = l.Biorhythm.Phase.ToString(),
                    energy = l.Biorhythm.EnergyLevel,
                    focus = l.Biorhythm.FocusLevel,
                    creativity = l.Biorhythm.CreativityLevel
                },
                hormones = new
                {
                    dopamine = l.Hormones.Dopamine,
                    serotonin = l.Hormones.Serotonin,
                    cortisol = l.Hormones.Cortisol,
                    oxytocin = l.Hormones.Oxytocin
                },
                habits = l.Habits.Select(h => new
                {
                    name = h.Value.Name,
                    strength = h.Value.Strength,
                    frequency = h.Value.Frequency
                }).ToList()
            });
        });

        endpoints.MapGet("/api/dna/safety", (DNAOrchestrator dna) =>
        {
            var s = dna.Safety.GetStatus();
            return Results.Json(s);
        });

        endpoints.MapGet("/api/dna/narrative", async (DNAOrchestrator dna) =>
        {
            var narrative = dna.GenerateSelfNarrative();
            return Results.Json(new { narrative });
        });

        endpoints.MapPost("/api/dna/safety/posture", async (
            HttpContext context, DNAOrchestrator dna, CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<Dictionary<string, string>>(body);
            if (request != null && request.TryGetValue("posture", out var postureStr) &&
                Enum.TryParse<LTAI.DNA.Models.SafetyPosture>(postureStr, true, out var posture))
            {
                dna.Safety.SetPosture(posture);
                return Results.Json(new { posture = posture.ToString() });
            }
            return Results.Json(new { error = "Invalid posture" }, statusCode: 400);
        });

        endpoints.MapGet("/api/dna/world", (DNAOrchestrator dna) =>
            Results.Json(new { entities = dna.World.EntityCount, relations = dna.World.RelationCount, accuracy = dna.World.Accuracy }));

        endpoints.MapGet("/api/dna/predict", (DNAOrchestrator dna, string metric) =>
        {
            var forecast = dna.Predictor.Forecast(metric);
            var trending = dna.Predictor.GetTrending(3);
            return Results.Json(new { metric, forecast, trending });
        });

        endpoints.MapGet("/api/dna/memory", (DNAOrchestrator dna, string? query) =>
        {
            var recall = query != null ? dna.MTT.Recall(query) : $"Episodes: {dna.MTT.EpisodeCount}";
            return Results.Json(new { recall });
        });

        endpoints.MapGet("/api/dna/multistream", (DNAOrchestrator dna) =>
            Results.Json(dna.MultiStream.Stats()));

        endpoints.MapGet("/api/dna/surprise", (DNAOrchestrator dna) =>
            Results.Json(dna.SurpriseGate.Stats()));

        endpoints.MapGet("/api/dna/meta/memory", (DNAOrchestrator dna) =>
            Results.Json(dna.MetaMemory.Stats()));

        endpoints.MapGet("/api/dna/meta/optimizer", (DNAOrchestrator dna) =>
            Results.Json(dna.MetaOptimizer.Stats()));

        endpoints.MapGet("/api/dna/meta/calibration", (DNAOrchestrator dna) =>
        {
            var (precision, recall, calibration) = dna.MetaMemory.GatingCalibration();
            var misgated = dna.MetaMemory.MisgatedStrategies();
            return Results.Json(new { precision, recall, calibration, misgated });
        });

        endpoints.MapGet("/api/dna/rlvr", (DNAOrchestrator dna) =>
            Results.Json(dna.RLVR.Stats()));

        endpoints.MapGet("/api/dna/compiler", (DNAOrchestrator dna) =>
            Results.Json(dna.Compiler.Stats()));

        endpoints.MapPost("/api/dna/compiler/recompile", (DNAOrchestrator dna) =>
        {
            var removed = dna.Compiler.RecompileStale();
            return Results.Json(new { stale_removed = removed });
        });

        endpoints.MapGet("/api/dna/hormones", (DNAOrchestrator dna) =>
            Results.Json(dna.Life.Hormones));

        endpoints.MapGet("/api/dna/biorhythm", (DNAOrchestrator dna) =>
            Results.Json(dna.Life.Biorhythm));

        endpoints.MapGet("/api/dna/identity", (DNAOrchestrator dna) =>
        {
            var idPrompt = dna.IdentityNarrative.GetIdentityPrompt();
            var stats = dna.IdentityNarrative.GetStats();
            return Results.Json(new { identity_prompt = idPrompt, stats });
        });

        endpoints.MapGet("/api/dna/personality", (DNAOrchestrator dna) =>
        {
            var stats = dna.Personality.GetStats();
            var stylePrompt = dna.Personality.GenerateStylePrompt();
            return Results.Json(new { stats, style_prompt = stylePrompt });
        });

        endpoints.MapGet("/api/dna/context", (DNAOrchestrator dna, string? text) =>
        {
            if (text != null) return Results.Json(dna.ContextEngineer.FullContextAudit(text));
            return Results.Json(dna.ContextEngineer.Flywheel.GetStats());
        });

        endpoints.MapGet("/api/dna/local-intelligence", (DNAOrchestrator dna, string? query) =>
        {
            if (query != null)
            {
                var response = dna.LocalIntelligence.Respond(query);
                return Results.Json(new { content = response.Content, tier = response.Tier.ToString(), confidence = response.Confidence });
            }
            return Results.Json(dna.LocalIntelligence.GetStats());
        });
    }
}
