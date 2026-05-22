using System.Text.Json;
using LTAI.Planning.Models;
using LTAI.Planning.Planning;
using LTAI.Planning.Quality;
using LTAI.Planning.Session;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Planning;

public static class ExecutionEndpoints
{
    public static IEndpointRouteBuilder MapExecutionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/execution/plan/diffuse", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            var request = JsonSerializer.Deserialize<DiffuseRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.Intent))
                return Results.Json(new { error = "Intent is required" }, statusCode: 400);

            var planner = endpoints.ServiceProvider.GetRequiredService<DiffusionPlanner>();
            var plan = await planner.Refine(request.Intent, request.Domain ?? "general");
            return Results.Json(plan);
        });

        endpoints.MapPost("/api/execution/plan/gtsm", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            var request = JsonSerializer.Deserialize<GtsmRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.Task))
                return Results.Json(new { error = "Task is required" }, statusCode: 400);

            var mode = Enum.TryParse<GTSMMode>(request.Mode, ignoreCase: true, out var parsed) ? parsed : GTSMMode.Auto;

            var planner = endpoints.ServiceProvider.GetRequiredService<GtsmPlanner>();
            var trajectory = planner.Plan(request.Task, mode, request.Domain ?? "general", request.MaxSteps > 0 ? request.MaxSteps : 8);
            return Results.Json(trajectory);
        });

        endpoints.MapPost("/api/execution/plan/checkpoint/save", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            var state = JsonSerializer.Deserialize<CheckpointState>(body);

            if (state == null || string.IsNullOrWhiteSpace(state.SessionId))
                return Results.Json(new { error = "CheckpointState with SessionId is required" }, statusCode: 400);

            var checkpoint = endpoints.ServiceProvider.GetRequiredService<TaskCheckpoint>();
            await checkpoint.SaveAsync(state.SessionId, state);
            return Results.Json(new { saved = true, sessionId = state.SessionId, version = state.Version });
        });

        endpoints.MapGet("/api/execution/plan/checkpoint/{sessionId}", async (string sessionId, HttpContext context) =>
        {
            var checkpoint = endpoints.ServiceProvider.GetRequiredService<TaskCheckpoint>();
            var state = await checkpoint.LoadAsync(sessionId);
            if (state is null)
                return Results.NotFound(new { error = $"Checkpoint {sessionId} not found" });
            return Results.Json(state);
        });

        endpoints.MapPost("/api/execution/plan/checkpoint/{sessionId}/resume", async (string sessionId, HttpContext context) =>
        {
            var checkpoint = endpoints.ServiceProvider.GetRequiredService<TaskCheckpoint>();
            var state = await checkpoint.ResumeAsync(sessionId);
            if (state is null)
                return Results.NotFound(new { error = $"Checkpoint {sessionId} not found" });
            return Results.Json(state);
        });

        endpoints.MapGet("/api/execution/plan/checkpoint/list", () =>
        {
            var checkpoint = endpoints.ServiceProvider.GetRequiredService<TaskCheckpoint>();
            var sessions = checkpoint.ListSessions();
            var items = sessions.Select(s => new { id = s.id, savedAt = s.savedAt }).ToList();
            return Results.Json(items);
        });

        endpoints.MapGet("/api/execution/plan/budget", () =>
        {
            var costAware = endpoints.ServiceProvider.GetRequiredService<CostAware>();
            return Results.Json(costAware.Status());
        });

        endpoints.MapPost("/api/execution/plan/audit", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            var request = JsonSerializer.Deserialize<AuditRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.PlanId))
                return Results.Json(new { error = "PlanId is required" }, statusCode: 400);

            var steps = (request.Steps ?? new())
                .Select(s => (s.id ?? "", s.desc ?? ""))
                .ToList();

            var engine = endpoints.ServiceProvider.GetRequiredService<CoFEECognitiveEngine>();
            var audit = engine.AuditPlan(request.PlanId, steps, request.Goal ?? "");
            return Results.Json(audit);
        });

        endpoints.MapPost("/api/execution/session/save", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            var state = JsonSerializer.Deserialize<SessionState>(body);

            if (state == null || string.IsNullOrWhiteSpace(state.SessionId))
                return Results.Json(new { error = "SessionState with SessionId is required" }, statusCode: 400);

            var session = endpoints.ServiceProvider.GetRequiredService<SessionManager>();
            await session.SaveAsync(state);
            return Results.Json(new { saved = true, sessionId = state.SessionId });
        });

        endpoints.MapGet("/api/execution/session/{sessionId}", async (string sessionId) =>
        {
            var session = endpoints.ServiceProvider.GetRequiredService<SessionManager>();
            var state = await session.LoadAsync(sessionId);
            if (state is null)
                return Results.NotFound(new { error = $"Session {sessionId} not found" });
            return Results.Json(state);
        });

        endpoints.MapGet("/api/execution/session/list", () =>
        {
            var session = endpoints.ServiceProvider.GetRequiredService<SessionManager>();
            var sessions = session.ListSessions();
            return Results.Json(sessions);
        });

        endpoints.MapPost("/api/execution/session/{sessionId}/archive", (string sessionId) =>
        {
            var session = endpoints.ServiceProvider.GetRequiredService<SessionManager>();
            session.Archive(sessionId);
            return Results.Json(new { archived = true, sessionId });
        });

        endpoints.MapPost("/api/execution/sidegit/preturn", () =>
        {
            var sideGit = endpoints.ServiceProvider.GetRequiredService<SideGit>();
            var snapshot = sideGit.PreTurn();
            return Results.Json(snapshot);
        });

        endpoints.MapPost("/api/execution/sidegit/postturn/{turnId}", (string turnId) =>
        {
            var sideGit = endpoints.ServiceProvider.GetRequiredService<SideGit>();
            var snapshot = sideGit.PostTurn(turnId);
            return Results.Json(snapshot);
        });

        endpoints.MapGet("/api/execution/quality/pareto", () =>
        {
            var landscape = endpoints.ServiceProvider.GetRequiredService<FitnessLandscape>();
            return Results.Json(landscape.GetParetoFront());
        });

        endpoints.MapPost("/api/execution/quality/rank/analyze", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            var request = JsonSerializer.Deserialize<RankAnalyzeRequest>(body);

            if (request == null || request.Population == null || request.Population.Count == 0)
                return Results.Json(new { error = "Population list is required" }, statusCode: 400);

            var monitor = endpoints.ServiceProvider.GetRequiredService<RankMonitor>();
            var snapshot = monitor.Analyze(request.Population, f => f);
            return Results.Json(snapshot);
        });

        endpoints.MapPost("/api/execution/quality/delegate/select", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            var request = JsonSerializer.Deserialize<DelegateSelectRequest>(body);

            if (request == null || request.Candidates == null || request.Candidates.Count == 0)
                return Results.Json(new { error = "Candidates list is required" }, statusCode: 400);

            var delegator = endpoints.ServiceProvider.GetRequiredService<ThompsonDelegator>();
            var selected = delegator.SelectAgent(request.Candidates);
            return Results.Json(new { selected });
        });

        endpoints.MapPost("/api/execution/compress", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            var request = JsonSerializer.Deserialize<CompressRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.Output))
                return Results.Json(new { error = "Output is required" }, statusCode: 400);

            var compressor = endpoints.ServiceProvider.GetRequiredService<TerminalCompressor>();
            var result = compressor.Compress(request.Output, request.Command ?? "", request.Namespace ?? "");
            return Results.Json(result);
        });

        endpoints.MapGet("/api/execution/compress/rules", () =>
        {
            var pool = endpoints.ServiceProvider.GetRequiredService<GlobalRulePool>();
            var rules = pool.Query();
            return Results.Json(rules);
        });

        endpoints.MapPost("/api/execution/clarify", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            var request = JsonSerializer.Deserialize<ClarifyRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.Input))
                return Results.Json(new { error = "Input is required" }, statusCode: 400);

            var clarifier = endpoints.ServiceProvider.GetRequiredService<Clarifier>();
            var clarifications = clarifier.Analyze(request.Input, request.Domain ?? "general");
            return Results.Json(clarifications);
        });

        endpoints.MapPost("/api/execution/skill/detect-missing", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            var request = JsonSerializer.Deserialize<DetectMissingRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.Output))
                return Results.Json(new { error = "Output is required" }, statusCode: 400);

            var resolver = endpoints.ServiceProvider.GetRequiredService<AutoSkillResolver>();
            var skills = resolver.DetectMissing(request.Output, request.Task ?? "");
            return Results.Json(skills);
        });

        return endpoints;
    }
}

public sealed record DiffuseRequest
{
    public string Intent { get; init; } = "";
    public string? Domain { get; init; }
}

public sealed record GtsmRequest
{
    public string Task { get; init; } = "";
    public string? Domain { get; init; }
    public string? Mode { get; init; }
    public int MaxSteps { get; init; }
}

public sealed record EvolveRequest
{
    public List<EvolutionCandidate>? Candidates { get; init; }
}

public sealed record AuditRequest
{
    public string PlanId { get; init; } = "";
    public List<AuditStep>? Steps { get; init; }
    public string? Goal { get; init; }
}

public sealed record AuditStep
{
    public string? id { get; init; }
    public string? desc { get; init; }
}

public sealed record RankAnalyzeRequest
{
    public List<double>? Population { get; init; }
}

public sealed record DelegateSelectRequest
{
    public List<string>? Candidates { get; init; }
}

public sealed record CompressRequest
{
    public string Output { get; init; } = "";
    public string? Command { get; init; }
    public string? Namespace { get; init; }
}

public sealed record ClarifyRequest
{
    public string Input { get; init; } = "";
    public string? Domain { get; init; }
}

public sealed record DetectMissingRequest
{
    public string Output { get; init; } = "";
    public string? Task { get; init; }
}
