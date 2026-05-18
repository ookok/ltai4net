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

        endpoints.MapGet("/api/dna/evolution", (DNAOrchestrator dna) =>
        {
            var e = dna.Evolution;
            return Results.Json(new
            {
                phase = e.Phase.ToString(),
                generation = e.CurrentGenome.Generation,
                fitness = e.CurrentGenome.FitnessScore,
                genes = e.CurrentGenome.Genes.ToDictionary(
                    g => g.Key,
                    g => new { expression = g.Value.Expression, mutation_rate = g.Value.MutationRate }),
                population_size = e.Population.Count
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

        endpoints.MapPost("/api/dna/evolve", async (
            HttpContext context,
            DNAOrchestrator dna,
            CancellationToken cancellationToken) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync(cancellationToken);
                var signals = JsonSerializer.Deserialize<Dictionary<string, double>>(body);

                if (signals != null)
                    await dna.Evolution.EvolveAsync(signals, cancellationToken);

                return Results.Json(new { success = true, fitness = dna.Evolution.CurrentGenome.FitnessScore });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        endpoints.MapPost("/api/dna/safety/posture", async (
            HttpContext context,
            DNAOrchestrator dna,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            var request = JsonSerializer.Deserialize<Dictionary<string, string>>(body);

            if (request != null && request.TryGetValue("posture", out var postureStr) &&
                Enum.TryParse<LTAI.DNA.Models.SafetyPosture>(postureStr, true, out var posture))
            {
                dna.Safety.SetPosture(posture);
                return Results.Json(new { posture = posture.ToString() });
            }

            return Results.Json(new { error = "Invalid posture" }, statusCode: 400);
        });
    }
}
