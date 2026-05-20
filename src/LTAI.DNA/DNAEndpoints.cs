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

        endpoints.MapGet("/api/dna/world", (DNAOrchestrator dna) =>
        {
            return Results.Json(new
            {
                entities = dna.World.EntityCount,
                relations = dna.World.RelationCount,
                accuracy = dna.World.Accuracy
            });
        });

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

        endpoints.MapGet("/api/dna/foresight", (DNAOrchestrator dna, string action) =>
        {
            var (proceed, reason) = dna.Foresight.EvaluateAction(action);
            return Results.Json(new { action, proceed, reason });
        });

        endpoints.MapGet("/api/dna/focus", (DNAOrchestrator dna) =>
        {
            var distribution = dna.Focus.GetFocusDistribution();
            return Results.Json(new { distribution });
        });

        endpoints.MapGet("/api/dna/godel", (DNAOrchestrator dna, string statement) =>
        {
            var reflection = dna.Godel.Reflect(statement);
            return Results.Json(new { statement, reflection, depth = dna.Godel.Depth });
        });

        endpoints.MapGet("/api/dna/emergence", (DNAOrchestrator dna) =>
        {
            var stats = dna.Emergence.Stats();
            return Results.Json(new
            {
                phase = stats["phase"],
                total_experiences = stats["total_experiences"],
                contemplation_count = stats["contemplation_count"],
                contradictions = stats["contradictions"],
                readiness = stats["latest_readiness"],
                is_conscious = stats["is_conscious"],
                narrative = dna.Emergence.Narrative(),
                events = dna.Emergence.GetEmergenceEvents(10)
            });
        });

        endpoints.MapGet("/api/dna/shesha", (DNAOrchestrator dna) =>
        {
            return Results.Json(new
            {
                stats = dna.Shesha.Stats(),
                society = dna.Shesha.GetSocietySummary(),
                heads = dna.Shesha.ListHeads().Select(h => new
                {
                    id = h.Id,
                    name = h.Name,
                    role = h.Role.ToString(),
                    phase = h.Phase.ToString(),
                    success_rate = h.SuccessRate,
                    total_tasks = h.TotalTasks,
                    successful_tasks = h.SuccessfulTasks,
                    traits = h.Traits
                }).ToList()
            });
        });

        endpoints.MapGet("/api/dna/shesha/society/evolve", (DNAOrchestrator dna) =>
        {
            var result = dna.Shesha.EvolveSociety();
            return Results.Json(result);
        });

        endpoints.MapGet("/api/dna/play", (DNAOrchestrator dna) =>
        {
            var stats = dna.Play.Stats();
            return Results.Json(stats);
        });

        endpoints.MapGet("/api/dna/multistream", (DNAOrchestrator dna) =>
        {
            var stats = dna.MultiStream.Stats();
            return Results.Json(stats);
        });

        endpoints.MapGet("/api/dna/surprise", (DNAOrchestrator dna) =>
        {
            var stats = dna.SurpriseGate.Stats();
            return Results.Json(stats);
        });

        endpoints.MapGet("/api/dna/meta/memory", (DNAOrchestrator dna) =>
        {
            var stats = dna.MetaMemory.Stats();
            return Results.Json(stats);
        });

        endpoints.MapGet("/api/dna/meta/optimizer", (DNAOrchestrator dna) =>
        {
            var stats = dna.MetaOptimizer.Stats();
            return Results.Json(stats);
        });

        endpoints.MapGet("/api/dna/meta/strategy", (DNAOrchestrator dna) =>
        {
            var status = dna.MetaStrategyEngine.GetStatus();
            return Results.Json(status);
        });

        endpoints.MapGet("/api/dna/meta/calibration", (DNAOrchestrator dna) =>
        {
            var (precision, recall, calibration) = dna.MetaMemory.GatingCalibration();
            var misgated = dna.MetaMemory.MisgatedStrategies();
            return Results.Json(new { precision, recall, calibration, misgated });
        });

        endpoints.MapGet("/api/dna/rlvr", (DNAOrchestrator dna) =>
        {
            var stats = dna.RLVR.Stats();
            return Results.Json(stats);
        });

        endpoints.MapGet("/api/dna/compiler", (DNAOrchestrator dna) =>
        {
            var stats = dna.Compiler.Stats();
            return Results.Json(stats);
        });

        endpoints.MapPost("/api/dna/compiler/recompile", (DNAOrchestrator dna) =>
        {
            var removed = dna.Compiler.RecompileStale();
            return Results.Json(new { stale_removed = removed });
        });

        endpoints.MapGet("/api/dna/hormones", (DNAOrchestrator dna) =>
        {
            var stats = dna.HormoneNetwork.GetStats();
            return Results.Json(stats);
        });

        endpoints.MapGet("/api/dna/biorhythm", (DNAOrchestrator dna) =>
        {
            var snapshot = dna.BiorhythmEngine.GetSnapshot();
            return Results.Json(snapshot);
        });

        endpoints.MapGet("/api/dna/immune", (DNAOrchestrator dna) =>
        {
            var stats = dna.ImmuneDefense.GetStats();
            return Results.Json(stats);
        });

        endpoints.MapPost("/api/dna/immune/check", async (
            HttpContext context,
            DNAOrchestrator dna,
            CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var request = JsonSerializer.Deserialize<Dictionary<string, string>>(body);
            var input = request?.GetValueOrDefault("input", "") ?? "";
            var result = dna.ImmuneDefense.CheckInput(input);
            return Results.Json(new
            {
                passed = result.Action == Models.ThreatAction.Log,
                action = result.Action.ToString(),
                threat_level = result.ThreatLevel,
                type = result.Type.ToString(),
                pattern = result.MatchedPattern,
                antibody = result.MatchedAntibody,
            });
        });

        endpoints.MapGet("/api/dna/identity", (DNAOrchestrator dna) =>
        {
            var idPrompt = dna.IdentityNarrative.GetIdentityPrompt();
            var constitution = dna.IdentityNarrative.GetConstitution();
            var cached = dna.IdentityNarrative.CachedNarrative;
            var stats = dna.IdentityNarrative.GetStats();
            return Results.Json(new { identity_prompt = idPrompt, constitution, cached_narrative = cached, stats });
        });

        endpoints.MapGet("/api/dna/personality", (DNAOrchestrator dna) =>
        {
            var stats = dna.Personality.GetStats();
            var stylePrompt = dna.Personality.GenerateStylePrompt();
            return Results.Json(new { stats, style_prompt = stylePrompt });
        });

        endpoints.MapGet("/api/dna/context", (DNAOrchestrator dna, string? text) =>
        {
            if (text != null)
            {
                var audit = dna.ContextEngineer.FullContextAudit(text);
                return Results.Json(audit);
            }
            var stats = dna.ContextEngineer.Flywheel.GetStats();
            return Results.Json(stats);
        });

        endpoints.MapGet("/api/dna/local-intelligence", (DNAOrchestrator dna, string? query) =>
        {
            if (query != null)
            {
                var response = dna.LocalIntelligence.Respond(query);
                return Results.Json(new
                {
                    content = response.Content,
                    tier = response.Tier.ToString(),
                    confidence = response.Confidence,
                });
            }
            var stats = dna.LocalIntelligence.GetStats();
            return Results.Json(stats);
        });

        endpoints.MapGet("/api/dna/presence", (DNAOrchestrator dna) =>
        {
            var session = dna.LivingPresence.SessionStart();
            var selfCheck = dna.LivingPresence.PresenceSelfCheck();
            return Results.Json(new { session, self_check = selfCheck });
        });

        endpoints.MapPost("/api/dna/presence/gaze", (DNAOrchestrator dna) =>
        {
            var gaze = dna.LivingPresence.Gaze.ShouldGaze(60, 0, 0, false, null);
            return Results.Json(gaze);
        });
    }
}
