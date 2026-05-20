using LTAI.MAF.Evolution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Text.Json;

namespace LTAI.MAF;

public static class AHEEndpoints
{
    public static void MapAHEEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // harness
        endpoints.MapGet("/api/harness/manifest", (HarnessSnapshot snapshot) =>
        {
            var manifest = snapshot.LoadLatest();
            return manifest != null ? Results.Json(manifest) : Results.Json(new { error = "No manifest captured" }, statusCode: 404);
        });

        endpoints.MapPost("/api/harness/snapshot", (HarnessSnapshot snapshot) =>
        {
            var manifest = snapshot.Capture();
            return Results.Json(new { captured = true, manifest.ToolCount, manifest.Components.Count });
        });

        endpoints.MapGet("/api/harness/snapshots", (HarnessSnapshot snapshot) =>
        {
            var snaps = snapshot.ListSnapshots();
            return Results.Json(new { count = snaps.Count, snapshots = snaps });
        });

        endpoints.MapGet("/api/harness/experience", (ExperienceDebugger debugger) =>
        {
            var report = debugger.Analyze();
            return Results.Json(report);
        });

        endpoints.MapGet("/api/harness/experience/report", async (ExperienceDebugger debugger) =>
        {
            var report = debugger.Analyze();
            return Results.Text(report.ToMarkdown(), "text/markdown");
        });

        endpoints.MapGet("/api/harness/decisions", (DecisionLog log) =>
        {
            return Results.Json(new { stats = log.GetStats(), edits = log.Edits.Take(50) });
        });

        endpoints.MapPost("/api/harness/decisions/verify", async (HttpContext context, DecisionLog log) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            var request = JsonSerializer.Deserialize<JsonElement>(body);
            var editId = request.TryGetProperty("editId", out var id) ? id.GetString() : null;
            var held = request.TryGetProperty("predictionHeld", out var h) && h.GetBoolean();
            var result = request.TryGetProperty("result", out var r) ? r.GetString() : null;
            var improvement = request.TryGetProperty("actualImprovement", out var imp) && imp.TryGetDouble(out var d) ? d : 0.0;
            if (string.IsNullOrWhiteSpace(editId))
                return Results.Json(new { error = "editId required" }, statusCode: 400);
            var verified = log.VerifyEdit(editId, held, result, improvement);
            return verified != null ? Results.Json(verified) : Results.Json(new { error = "Edit not found" }, statusCode: 404);
        });

        // plugins
        endpoints.MapGet("/api/plugins", (PluginRegistry registry) =>
        {
            return Results.Json(new { count = registry.Plugins.Count, plugins = registry.Plugins });
        });

        endpoints.MapPost("/api/plugins/discover", (PluginRegistry registry) =>
        {
            registry.Discover();
            return Results.Json(new { discovered = registry.Plugins.Count });
        });

        endpoints.MapGet("/api/plugins/{name}", (string name, PluginRegistry registry) =>
        {
            var plugin = registry.FindByName(name);
            return plugin != null ? Results.Json(plugin) : Results.Json(new { error = "Plugin not found" }, statusCode: 404);
        });

        endpoints.MapPost("/api/plugins/install", async (HttpContext context, PluginRegistry registry) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            var manifest = JsonSerializer.Deserialize<PluginManifest>(body);
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.Name))
                return Results.Json(new { error = "Valid plugin manifest required" }, statusCode: 400);
            registry.Install(manifest.Name, manifest);
            return Results.Json(new { installed = manifest.Name, agents = manifest.Agents, tools = manifest.Tools });
        });

        // review agents
        endpoints.MapGet("/api/review/agents", () =>
        {
            var agents = new[]
            {
                new { name = ReviewAgentPrompts.CommentAnalyzer.Name, triggers = ReviewAgentPrompts.CommentAnalyzer.Triggers, description = "Code comment accuracy and documentation freshness" },
                new { name = ReviewAgentPrompts.TestAnalyzer.Name, triggers = ReviewAgentPrompts.TestAnalyzer.Triggers, description = "Test coverage quality and edge case detection" },
                new { name = ReviewAgentPrompts.SilentFailureHunter.Name, triggers = ReviewAgentPrompts.SilentFailureHunter.Triggers, description = "Error handling audit and silent failure detection" },
                new { name = ReviewAgentPrompts.TypeDesignAnalyzer.Name, triggers = ReviewAgentPrompts.TypeDesignAnalyzer.Triggers, description = "Type design quality and invariant analysis" },
                new { name = ReviewAgentPrompts.CodeReviewer.Name, triggers = ReviewAgentPrompts.CodeReviewer.Triggers, description = "General code quality review and bug detection" },
                new { name = ReviewAgentPrompts.CodeSimplifier.Name, triggers = ReviewAgentPrompts.CodeSimplifier.Triggers, description = "Code simplification and refactoring suggestions" }
            };
            return Results.Json(new { count = agents.Length, agents });
        });

        endpoints.MapPost("/api/review/route", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            var request = JsonSerializer.Deserialize<JsonElement>(body);
            var query = request.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
            var (agent, instructions, confidence) = ReviewAgentRouter.RouteReview(query);
            return Results.Json(new { query, agent, confidence, instructions });
        });

        // fitness
        // evolution
        endpoints.MapGet("/api/harness/evolution/components", (HarnessEvolutionEngine engine) =>
        {
            return Results.Json(new { count = engine.Components.Count, components = engine.Components.Select(c => new { c.ComponentName, c.CurrentHash }) });
        });

        endpoints.MapPost("/api/harness/evolution/iterate", async (HarnessEvolutionEngine engine, IServiceProvider sp, CancellationToken ct) =>
        {
            var result = await engine.RunIterationAsync(sp, ct);
            return Results.Json(result);
        });

        endpoints.MapPost("/api/harness/evolution/verify", async (HarnessEvolutionEngine engine, IServiceProvider sp, CancellationToken ct) =>
        {
            await engine.VerifyPendingEditsAsync(sp, ct);
            return Results.Json(new { verified = true });
        });

        // token savings
        endpoints.MapGet("/api/harness/token-savings", (LTAI.Vector.Knowledge.TokenSavingsTracker tracker) =>
        {
            var stats = tracker.GetStats();
            return Results.Json(new
            {
                today = new { saved = stats.TodaySaved, calls = stats.TodayCalls },
                week = new { saved = stats.WeekSaved, calls = stats.WeekCalls },
                total = new { saved = stats.TotalSaved, calls = stats.TotalCalls },
                avgSavingRate = stats.AvgSavingRate
            });
        });

        // code search rerank test
        endpoints.MapPost("/api/search/code-rerank", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            var req = JsonSerializer.Deserialize<JsonElement>(body);
            var query = req.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
            var queryType = LTAI.Vector.Knowledge.CodeSearchReranker.ClassifyQueryType(query);
            return Results.Json(new { query, queryType, adaptiveWeights = queryType == "symbol" ? "lexical-heavy" : "balanced" });
        });

        endpoints.MapGet("/api/harness/fitness", (DecisionLog log, HarnessSnapshot snapshot) =>
        {
            var stats = log.GetStats();
            var manifest = snapshot.LoadLatest();
            return Results.Json(new
            {
                decisionStats = stats,
                toolCount = manifest?.ToolCount ?? 0,
                middlewareChain = manifest?.MiddlewareChain ?? new List<string>(),
                degradationChain = manifest?.DegradationChain ?? new Dictionary<string, string>()
            });
        });
    }
}
