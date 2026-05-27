using System.Text.Json;
using LTAI.Core.Acceleration;
using LTAI.Core.Governors;
using LTAI.Core.Life;
using LTAI.Core.Prefs;
using LTAI.Core.Resilience;
using LTAI.Core.System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LTAI.Core;

public static class CoreEndpoints
{
    public static void MapCoreEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/core/gpu", () =>
            Results.Json(HardwareAcceleration.Instance.Stats()));

        endpoints.MapPost("/api/core/compress", async (HttpContext context, CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<CompressRequest>(body);
            if (request == null || string.IsNullOrWhiteSpace(request.Content))
                return Results.Json(new { error = "content required" }, statusCode: 400);
            var (compressed, filter, savedPct) = TokenCompressor.Compress(request.Content);
            return Results.Json(new { compressed, filter, saved_pct = savedPct });
        });

        endpoints.MapPost("/api/core/twin/simulate", async (HttpContext context, CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<TwinSimulateRequest>(body);
            if (request == null)
                return Results.Json(new { error = "simulation parameters required" }, statusCode: 400);
            var snapshot = new TwinSnapshot(
                request.SynapseWeights ?? new Dictionary<string, double>(),
                new Dictionary<string, string>(),
                request.PoolHealth,
                request.EconStats ?? new Dictionary<string, double>(),
                new Dictionary<string, double>(),
                DateTime.UtcNow);
            var result = DigitalTwin.Instance.Simulate(snapshot, request.Hours ?? 24, request.Checkpoints ?? 6);
            return Results.Json(result);
        });

        endpoints.MapPost("/api/core/behavior/execute", (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = reader.ReadToEnd();
            var request = JsonSerializer.Deserialize<BehaviorExecuteRequest>(body);
            if (request == null)
                return Results.Json(new { error = "tree and context required" }, statusCode: 400);
            var root = BehaviorTreeFactory.BuildAgenticTree(
                request.TaskSteps ?? new List<string>(),
                request.FallbackSteps ?? new List<string>(),
                request.PreChecks ?? new List<string>());
            var ctx = new TreeContext(
                request.Context ?? "",
                new Dictionary<string, string>(),
                new List<string>(),
                new List<string>(),
                new List<string>(),
                0,
                10);
            var status = root.Tick(ctx);
            return Results.Json(new { status = status.ToString(), history = ctx.History, results = ctx.Results, errors = ctx.Errors });
        });

        endpoints.MapGet("/api/core/growth", () =>
            Results.Json(AutonomousGrowth.Instance.Status()));

        endpoints.MapGet("/api/core/plasticity", () =>
            Results.Json(SynapticPlasticity.Instance.Stats()));

        endpoints.MapGet("/api/core/health", (HttpContext context) =>
        {
            var (reportCount, trustCount, overallHealth) = SystemHealth.Instance.Stats();
            var kernel = context.RequestServices.GetService(typeof(IMicroKernel)) as IMicroKernel;
            var auditTrail = kernel?.GetAuditTrail(10);
            var vitals = kernel?.GetVitalSigns();
            var aggregated = kernel?.GetAggregatedVitals();
            var snapshots = kernel?.GetSnapshots();
            return Results.Json(new
            {
                report_count = reportCount,
                trust_profile_count = trustCount,
                overall_health = overallHealth,
                microkernel_healthy = kernel?.IsHealthy ?? true,
                microkernel_vitals = aggregated != null ? new
                {
                    aggregated.Primitive,
                    aggregated.CallCount,
                    aggregated.SuccessRate,
                    aggregated.AvgLatencyMs,
                    aggregated.P50LatencyMs,
                    aggregated.P95LatencyMs,
                    aggregated.P99LatencyMs
                } : null,
                microkernel_per_primitive = vitals?.Select(v => new
                {
                    v.Primitive,
                    v.CallCount,
                    v.SuccessCount,
                    v.FailureCount,
                    v.SuccessRate,
                    v.AvgLatencyMs,
                    v.P50LatencyMs,
                    v.P95LatencyMs
                }).ToList(),
                microkernel_snapshots = snapshots?.Select(s => new
                {
                    s.Id, s.Description, s.CapturedAt,
                    configCount = s.ConfigState.Count,
                    geneCount = s.ActiveGeneIds.Count
                }).ToList(),
                microkernel_audit = auditTrail?.Select(a => new
                {
                    a.TraceId, a.Primitive, a.Success, a.ElapsedMs,
                    a.Summary, a.RiskScore
                }).ToList()
            });
        });

        endpoints.MapPost("/api/core/health/trust", async (HttpContext context, CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<TrustRequest>(body);
            if (request == null || string.IsNullOrWhiteSpace(request.AgentId))
                return Results.Json(new { error = "agentId required" }, statusCode: 400);
            SystemHealth.Instance.RecordTrust(request.AgentId, request.Success, request.Latency ?? 0, false);
            var score = SystemHealth.Instance.GetTrustScore(request.AgentId);
            return Results.Json(new { agent_id = request.AgentId, trust_score = score });
        });

        endpoints.MapGet("/api/core/resilience", () =>
            Results.Json(ResilienceBrain.Instance.Stats()));

        endpoints.MapGet("/api/core/shell/tools", () =>
            Results.Json(new { summary = ShellEnv.Instance.ProbeSummary() }));

        endpoints.MapPost("/api/core/shell/exec", async (HttpContext context, CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<ShellExecRequest>(body);
            if (request == null || string.IsNullOrWhiteSpace(request.Command))
                return Results.Json(new { error = "command required" }, statusCode: 400);
            var result = await ShellEnv.Instance.Execute(request.Command, request.Workdir ?? ".");
            return Results.Json(result);
        });

        endpoints.MapPost("/api/core/shield/input", async (HttpContext context, CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<ShieldInputRequest>(body);
            if (request == null || string.IsNullOrWhiteSpace(request.Text))
                return Results.Json(new { error = "text required" }, statusCode: 400);
            var result = PromptShield.Instance.SanitizeInput(request.Text);
            return Results.Json(result);
        });

        endpoints.MapPost("/api/core/shield/output", async (HttpContext context, CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<ShieldOutputRequest>(body);
            if (request == null || string.IsNullOrWhiteSpace(request.Text))
                return Results.Json(new { error = "text required" }, statusCode: 400);
            var result = PromptShield.Instance.CheckOutput(request.Text, request.Context ?? "public");
            return Results.Json(result);
        });

        endpoints.MapPost("/api/core/tree/read", async (HttpContext context, CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<TreeReadRequest>(body);
            if (request == null || string.IsNullOrWhiteSpace(request.Path))
                return Results.Json(new { error = "path required" }, statusCode: 400);
            var result = await ResourceTree.Instance.Read(request.Path).ConfigureAwait(false);
            return Results.Json(result);
        });

        endpoints.MapPost("/api/core/atomic/apply", async (HttpContext context, CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<AtomicApplyRequest>(body);
            if (request == null || request.Edits == null || request.Edits.Count == 0)
                return Results.Json(new { error = "edits required" }, statusCode: 400);
            var result = await AtomicModification.Instance.Apply(request.Edits, request.Reason ?? "");
            return Results.Json(result);
        });

        endpoints.MapPost("/api/core/scanner/discover", (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = reader.ReadToEnd();
            var request = JsonSerializer.Deserialize<ScannerDiscoverRequest>(body);
            if (request == null || string.IsNullOrWhiteSpace(request.Description))
                return Results.Json(new { error = "description required" }, statusCode: 400);
            var results = UniversalScanner.Instance.DiscoverFromDescription(request.Description);
            return Results.Json(results);
        });

        endpoints.MapGet("/api/core/prefs", () =>
            Results.Json(DpoPrefs.Instance.Stats()));

        endpoints.MapPost("/api/core/prefs/route", async (HttpContext context, CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<PrefsRouteRequest>(body);
            if (request == null || string.IsNullOrWhiteSpace(request.Entity))
                return Results.Json(new { error = "entity required" }, statusCode: 400);
            var candidates = request.Entity.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var choice = DpoPrefs.Instance.Router.RouteSkill(request.Context ?? "", candidates);
            return Results.Json(new { entity = request.Entity, context = request.Context, choice });
        });
    }
}

public sealed record CompressRequest
{
    public string Content { get; init; } = string.Empty;
}

public sealed record TwinSimulateRequest
{
    public Dictionary<string, double>? SynapseWeights { get; init; }
    public double PoolHealth { get; init; }
    public Dictionary<string, double>? EconStats { get; init; }
    public double? Hours { get; init; }
    public int? Checkpoints { get; init; }
}

public sealed record BehaviorExecuteRequest
{
    public List<string>? TaskSteps { get; init; }
    public List<string>? FallbackSteps { get; init; }
    public List<string>? PreChecks { get; init; }
    public string Context { get; init; } = string.Empty;
}

public sealed record TrustRequest
{
    public string AgentId { get; init; } = string.Empty;
    public bool Success { get; init; }
    public double? Latency { get; init; }
}

public sealed record ShellExecRequest
{
    public string Command { get; init; } = string.Empty;
    public string? Workdir { get; init; }
}

public sealed record ShieldInputRequest
{
    public string Text { get; init; } = string.Empty;
}

public sealed record ShieldOutputRequest
{
    public string Text { get; init; } = string.Empty;
    public string? Context { get; init; }
}

public sealed record TreeReadRequest
{
    public string Path { get; init; } = string.Empty;
}

public sealed record AtomicApplyRequest
{
    public Dictionary<string, string>? Edits { get; init; }
    public string? Reason { get; init; }
}

public sealed record ScannerDiscoverRequest
{
    public string Description { get; init; } = string.Empty;
}

public sealed record PrefsRouteRequest
{
    public string Entity { get; init; } = string.Empty;
    public string? Context { get; init; }
}
