using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using LTAI.Planning.Metrics.Audit;
using LTAI.Planning.Metrics.Evaluation;
using LTAI.Planning.Metrics.Monitoring;
using LTAI.Planning.Metrics.Policy;
using LTAI.Planning.Metrics.Safety;
using LTAI.Knowledge.Core;

namespace LTAI.Planning.Metrics;

public static class MetricsExtensions
{
    public static IServiceCollection AddLTAIMetrics(this IServiceCollection services)
    {
        services.AddSingleton<LTAIMetricsCollector>();

        services.AddSingleton(_ => AgentEval.Instance);
        services.AddSingleton(_ => EvalHarness.Instance);
        services.AddSingleton(_ => EvalDashboard.Instance.Value);
        services.AddSingleton(_ => StatisticalRealismValidator.Instance.Value);
        services.AddSingleton(_ => AuditLog.Instance);
        services.AddSingleton(_ => ActivityFeed.Instance.Value);
        services.AddSingleton(_ => ChangeManifest.Instance.Value);
        services.AddSingleton(_ => DynamicPolicyEngine.Instance);
        services.AddSingleton(_ => SystemMonitor.Instance);
        services.AddSingleton(_ => HarnessRegistry.Instance.Value);

        services.AddSingleton<RetrievalEvaluator>();
        services.AddSingleton<GoldenQueryManager>();
        services.AddSingleton<RetrievalMonitor>();
        services.AddSingleton<LayerIsolationEvaluator>(sp =>
        {
            var docStore = sp.GetRequiredService<DocumentStore>();
            var reranker = sp.GetRequiredService<Reranker>();
            return new LayerIsolationEvaluator(docStore, reranker);
        });

        return services;
    }

    public static WebApplication UseLTAIMetrics(this WebApplication app)
    {
        app.MapGet("/metrics", (LTAIMetricsCollector collector) =>
        {
            var s = collector.GetSnapshot();
            return Results.Text(
                "# HELP ltai_requests_total Total request count\n" +
                "# TYPE ltai_requests_total counter\n" +
                "ltai_requests_total " + s.TotalRequests + "\n" +
                "# HELP ltai_tokens_total Total tokens processed\n" +
                "# TYPE ltai_tokens_total counter\n" +
                "ltai_tokens_total " + s.TotalTokens + "\n" +
                "# HELP ltai_avg_latency_ms Average latency\n" +
                "# TYPE ltai_avg_latency_ms gauge\n" +
                "ltai_avg_latency_ms " + s.AvgLatencyMs.ToString("F1") + "\n" +
                "# HELP ltai_dna_awareness DNA awareness score\n" +
                "# TYPE ltai_dna_awareness gauge\n" +
                "ltai_dna_awareness " + s.Awareness.ToString("F3") + "\n" +
                "# HELP ltai_dna_fitness DNA fitness score\n" +
                "# TYPE ltai_dna_fitness gauge\n" +
                "ltai_dna_fitness " + s.Fitness.ToString("F3") + "\n" +
                "# HELP ltai_active_tasks Active tasks\n" +
                "# TYPE ltai_active_tasks gauge\n" +
                "ltai_active_tasks " + s.ActiveTasks + "\n" +
                "# HELP ltai_memory_mb Process memory\n" +
                "# TYPE ltai_memory_mb gauge\n" +
                "ltai_memory_mb " + s.MemoryMb + "\n",
                "text/plain; version=0.0.4");
        });

        app.MapGet("/api/metrics", (LTAIMetricsCollector collector) =>
            Results.Json(collector.GetSnapshot()));

        app.MapGet("/api/metrics/dashboard", () =>
            Results.Text(GrafanaDashboard.GenerateJson(), "application/json"));

        MapEvalEndpoints(app);
        MapAuditEndpoints(app);
        MapMonitoringEndpoints(app);
        MapPolicyEndpoints(app);

        return app;
    }

    private static void MapEvalEndpoints(WebApplication app)
    {
        app.MapPost("/api/eval/output", (AgentEval agentEval, EvalRequest req) =>
        {
            var result = agentEval.EvalOutput(req.Agent, req.Task, req.Output, req.Expected, req.Reference);
            return Results.Json(result);
        });

        app.MapPost("/api/eval/trace", (AgentEval agentEval, TraceEvalRequest req) =>
        {
            var result = agentEval.EvalTrace(req.Agent, req.Turns, req.HasRepeatedPatterns, req.AvgTurnDepth);
            return Results.Json(result);
        });

        app.MapPost("/api/eval/component", (AgentEval agentEval, ComponentEvalRequest req) =>
        {
            var result = agentEval.EvalComponent(req.Tool, req.Success, req.LatencyMs);
            return Results.Json(result);
        });

        app.MapPost("/api/eval/harness", (EvalHarness harness, HarnessEvalRequest req) =>
        {
            var report = harness.EvaluateTrajectory(req.TargetId, req.Output, req.ToolChain, req.CodeExecuted, req.LlmScores);
            return Results.Json(report);
        });

        app.MapPost("/api/eval/harness/gate", (EvalHarness harness, HarnessGateRequest req) =>
        {
            var (accepted, rejected) = harness.GateTrajectories(req.Trajectories);
            return Results.Json(new { accepted, rejected });
        });

        app.MapGet("/api/eval/dashboard", async (_) =>
            Results.Json(EvalDashboard.Instance.Value.GetSummary()));

        app.MapGet("/api/eval/dashboard/trend", async (string metric, int? window) =>
            Results.Json(EvalDashboard.Instance.Value.GetTrend(metric, window ?? 50)));

        app.MapGet("/api/eval/dashboard/alerts", async (_) =>
            Results.Json(EvalDashboard.Instance.Value.CheckAlerts()));

        app.MapPost("/api/eval/statistical/univariate", (StatisticalRealismValidator v, UnivariateRequest req) =>
        {
            var report = v.ValidateUnivariate(req.Synthetic, req.Reference, req.DimensionName, req.Bins);
            return Results.Json(report);
        });

        app.MapPost("/api/eval/statistical/report", (StatisticalRealismValidator v, StatReportRequest req) =>
        {
            var report = v.CreateReport(req.Target, req.Dimensions);
            return Results.Json(report);
        });

        app.MapGet("/api/eval/datasets", (AgentEval agentEval) =>
            Results.Json(agentEval.ListDatasets()));

        app.MapPost("/api/eval/datasets", (AgentEval agentEval, EvalDataset dataset) =>
        {
            agentEval.SaveDataset(dataset);
            return Results.Ok(new { saved = dataset.Id });
        });

        app.MapGet("/api/eval/drift/{agent}", (AgentEval agentEval, string agent) =>
            Results.Json(agentEval.CheckDrift(agent)));
    }

    private static void MapAuditEndpoints(WebApplication app)
    {
        app.MapPost("/api/audit/record", (AuditLog auditLog, AuditRecordRequest req) =>
        {
            var id = auditLog.Record(req.Stage, req.Phase, req.Operation, req.Target,
                req.Parameters, req.Result, req.SideEffects, req.Success, req.Error, req.DurationMs, req.SessionId, req.Metadata);
            return Results.Json(new { event_id = id });
        });

        app.MapGet("/api/audit/query", (AuditLog auditLog, string? sessionId, string? stage, string? operation, bool? success, DateTime? since, DateTime? until, int? limit) =>
            Results.Json(auditLog.Query(sessionId, stage, operation, success, since, until, limit ?? 100)));

        app.MapGet("/api/audit/session/{sessionId}", (AuditLog auditLog, string sessionId) =>
            Results.Json(auditLog.ReconstructChain(sessionId)));

        app.MapGet("/api/audit/failures/{sessionId}", (AuditLog auditLog, string sessionId) =>
            Results.Json(auditLog.GetFailureReport(sessionId)));

        app.MapGet("/api/audit/stats", (AuditLog auditLog) =>
            Results.Json(auditLog.GetStats()));
    }

    private static void MapMonitoringEndpoints(WebApplication app)
    {
        app.MapGet("/api/activity", async (ActivityFeed feed, int? limit, string? eventType, string? agent, string? severity) =>
        {
            EventType? type = eventType != null ? Enum.Parse<EventType>(eventType) : null;
            EventSeverity? sev = severity != null ? Enum.Parse<EventSeverity>(severity) : null;
            return Results.Json(feed.Query(limit ?? 20, type, agent, sev));
        });

        app.MapGet("/api/activity/summary", async (_) =>
            Results.Json(new { summary = ActivityFeed.Instance.Value.Summary24H() }));

        app.MapGet("/api/manifest/unverified", async (_) =>
            Results.Json(ChangeManifest.Instance.Value.GetUnverified()));

        app.MapGet("/api/manifest/falsified", async (int? limit) =>
            Results.Json(ChangeManifest.Instance.Value.GetFalsified(limit ?? 20)));

        app.MapGet("/api/manifest/report", async (_) =>
            Results.Json(ChangeManifest.Instance.Value.GetVerificationReport()));

        app.MapGet("/api/system/resource", async (_) =>
            Results.Json(SystemMonitor.Instance.Snapshot()));

        app.MapGet("/api/system/can-run", async (string taskName, bool? heavy) =>
            Results.Json(new { can_run = SystemMonitor.Instance.CanRunTask(taskName, heavy ?? false) }));

        app.MapPost("/api/harness/snapshot", async (HarnessRegistry h, FileRequest req) =>
        {
            var idx = h.Snapshot(req.Path, req.Trigger ?? "api");
            return Results.Json(new { index = idx });
        });

        app.MapGet("/api/harness/snapshots", async (HarnessRegistry h, string filePath) =>
            Results.Json(h.ListSnapshots(filePath)));
    }

    private static void MapPolicyEndpoints(WebApplication app)
    {
        app.MapGet("/api/policy/dashboard", async (_) =>
            Results.Json(DynamicPolicyEngine.Instance.GetDashboard()));

        app.MapPost("/api/policy/evaluate", async (_) =>
            Results.Json(DynamicPolicyEngine.Instance.Evaluate()));

        app.MapGet("/api/policy/experiments", async (_) =>
            Results.Json(DynamicPolicyEngine.Instance.GetAbResults("all")));

        app.MapPost("/api/policy/experiment", async (DynamicPolicyEngine engine, AbExperiment exp) =>
        {
            var result = engine.CreateExperiment(exp.Name, exp.StrategyA, exp.StrategyB, exp.Metric);
            return Results.Json(result);
        });
    }
}

public sealed record EvalRequest(string Agent, string Task, string Output, string? Expected, string? Reference);
public sealed record TraceEvalRequest(string Agent, int Turns, bool HasRepeatedPatterns, double AvgTurnDepth);
public sealed record ComponentEvalRequest(string Tool, bool Success, double LatencyMs);
public sealed record HarnessEvalRequest(string TargetId, string Output, List<string> ToolChain, bool CodeExecuted, List<double> LlmScores);
public sealed record HarnessGateRequest(List<(string, string, List<string>)> Trajectories);
public sealed record UnivariateRequest(List<double> Synthetic, List<double> Reference, string DimensionName, int Bins = 20);
public sealed record StatReportRequest(string Target, List<DimensionReport> Dimensions);
public sealed record AuditRecordRequest(string Stage, string Phase, string Operation, string Target, Dictionary<string, object?>? Parameters, string? Result, string? SideEffects, bool Success, string? Error, double DurationMs, string? SessionId, Dictionary<string, object?>? Metadata);
public sealed record FileRequest(string Path, string? Trigger);

public static class GrafanaDashboard
{
    public static string GenerateJson(string appName = "LTAI")
    {
        return @"{""title"":""" + appName + @""",""uid"":""ltai-main"",""panels"":[" +
            @"{""title"":""Request Rate"",""targets"":[{""expr"":""ltai_requests_total""}],""gridPos"":{""x"":0,""y"":0,""w"":8,""h"":6}}," +
            @"{""title"":""Avg Latency"",""targets"":[{""expr"":""ltai_avg_latency_ms""}],""gridPos"":{""x"":8,""y"":0,""w"":8,""h"":6}}," +
            @"{""title"":""Tokens"",""targets"":[{""expr"":""ltai_tokens_total""}],""gridPos"":{""x"":16,""y"":0,""w"":8,""h"":6}}," +
            @"{""title"":""DNA Awareness"",""targets"":[{""expr"":""ltai_dna_awareness""}],""gridPos"":{""x"":0,""y"":6,""w"":8,""h"":6}}," +
            @"{""title"":""DNA Fitness"",""targets"":[{""expr"":""ltai_dna_fitness""}],""gridPos"":{""x"":8,""y"":6,""w"":8,""h"":6}}," +
            @"{""title"":""Memory"",""targets"":[{""expr"":""ltai_memory_mb""}],""gridPos"":{""x"":16,""y"":6,""w"":8,""h"":6}}," +
            @"{""title"":""Active Tasks"",""targets"":[{""expr"":""ltai_active_tasks""}],""gridPos"":{""x"":0,""y"":12,""w"":8,""h"":6}}" +
            @"]}";
    }
}
