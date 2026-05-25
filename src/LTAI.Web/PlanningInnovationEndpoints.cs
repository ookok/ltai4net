using System.Text.Json;
using LTAI.Planning.HTN;
using LTAI.Planning.Trace;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LTAI.Web;

public static class PlanningInnovationEndpoints
{
    public static void MapPlanningInnovationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // ─── HTN Planner ───
        endpoints.MapPost("/api/htn/decompose", (HttpContext context, HTNPlanner planner) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = reader.ReadToEnd();
            var request = JsonSerializer.Deserialize<Dictionary<string, string>>(body);

            if (request == null || !request.TryGetValue("task", out var task))
                return Results.Json(new { error = "task is required" }, statusCode: 400);

            var domain = request.GetValueOrDefault("domain", "general");
            var toolsJson = request.GetValueOrDefault("tools", "km_search,shell,code_analyze");
            var tools = toolsJson.Split(',').Select(t => t.Trim()).ToList();

            var plan = planner.DecomposeTask(task, domain, tools);
            planner.StorePlan(plan, true);

            return Results.Json(new
            {
                plan_id = plan.Id,
                type = plan.Type.ToString(),
                children_count = plan.Children.Count,
                skeleton = BuildPlanJson(plan)
            });
        });

        endpoints.MapGet("/api/htn/templates", (string? domain, HTNPlanner planner) =>
        {
            var templates = domain != null
                ? planner.GetTemplatesByDomain(domain)
                : new List<PlanTemplate>();

            return Results.Json(new
            {
                count = templates.Count,
                templates = templates.Select(t => new
                {
                    t.Id, t.Name, t.Domain,
                    t.SuccessRate, t.SuccessCount, t.FailureCount,
                    sub_plan_count = t.SubPlanIds.Count,
                    t.LastUsedAt
                })
            });
        });

        endpoints.MapGet("/api/htn/stats", (HTNPlanner planner) =>
            Results.Json(planner.GetStats()));

        // ─── Explainability Trace ───
        endpoints.MapPost("/api/trace/start", (HttpContext context, TraceCollector collector) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = reader.ReadToEnd();
            var request = JsonSerializer.Deserialize<Dictionary<string, string>>(body);

            var sessionId = request?.GetValueOrDefault("session_id", "anon") ?? "anon";
            var query = request?.GetValueOrDefault("query", "") ?? "";

            var trace = collector.StartTrace(sessionId, query);
            return Results.Json(new
            {
                trace_id = trace.TraceId,
                session_id = trace.SessionId,
                trace_url = $"/api/trace/{trace.TraceId}"
            });
        });

        endpoints.MapPost("/api/trace/step", (HttpContext context, TraceCollector collector) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = reader.ReadToEnd();
            var request = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);

            if (request == null || !request.TryGetValue("trace_id", out var traceIdElem))
                return Results.Json(new { error = "trace_id is required" }, statusCode: 400);

            var traceId = traceIdElem.GetString() ?? "";
            var stepType = request.TryGetValue("type", out var typeElem)
                ? typeElem.GetString() ?? "tool_call" : "tool_call";

            collector.AddStep(traceId, new TraceStep
            {
                Type = stepType switch
                {
                    "intent" => TraceStepType.IntentRouting,
                    "tool" => TraceStepType.ToolCall,
                    "knowledge" => TraceStepType.KnowledgeRetrieval,
                    "model" => TraceStepType.ModelCall,
                    "verify" => TraceStepType.Verification,
                    _ => TraceStepType.ToolCall
                },
                Description = request.TryGetValue("description", out var d) ? d.GetString() ?? "" : "",
                AgentName = request.TryGetValue("agent", out var a) ? a.GetString() ?? "" : "",
                Confidence = request.TryGetValue("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0,
                Success = !request.TryGetValue("success", out var s) || s.GetBoolean()
            });

            return Results.Json(new { status = "recorded" });
        });

        endpoints.MapPost("/api/trace/complete", (HttpContext context, TraceCollector collector) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = reader.ReadToEnd();
            var request = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);

            if (request == null || !request.TryGetValue("trace_id", out var traceIdElem))
                return Results.Json(new { error = "trace_id is required" }, statusCode: 400);

            var traceId = traceIdElem.GetString() ?? "";
            collector.CompleteTrace(traceId,
                request.TryGetValue("response", out var r) ? r.GetString() ?? "" : "",
                request.TryGetValue("confidence", out var cf) && cf.ValueKind == JsonValueKind.Number ? cf.GetDouble() : 0,
                request.TryGetValue("verdict", out var v) ? v.GetString() ?? "UNKNOWN" : "UNKNOWN",
                request.TryGetValue("tokens", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0);

            return Results.Json(new { status = "completed" });
        });

        endpoints.MapGet("/api/trace/{traceId}", (string traceId, TraceCollector collector) =>
        {
            var trace = collector.GetTrace(traceId);
            if (trace == null)
                return Results.Json(new { error = "Trace not found" }, statusCode: 404);

            var decisionTree = collector.BuildDecisionTree(traceId);

            return Results.Json(new
            {
                trace.TraceId, trace.SessionId,
                trace.UserQuery, trace.FinalResponse,
                trace.OverallConfidence, trace.Verdict,
                trace.TotalTokens,
                started_at = trace.StartedAt,
                completed_at = trace.CompletedAt,
                duration_ms = (trace.CompletedAt - trace.StartedAt).TotalMilliseconds,
                step_count = trace.Steps.Count,
                steps = trace.Steps.OrderBy(s => s.Sequence).Select(s => new
                {
                    s.Sequence,
                    type = s.Type.ToString(),
                    s.Description,
                    s.Reasoning,
                    s.DataSource,
                    s.StandardReference,
                    s.Confidence,
                    s.LatencyMs,
                    s.Success,
                    s.ErrorMessage
                }),
                decision_tree = decisionTree
            });
        });

        endpoints.MapGet("/api/trace/recent", (int? count, TraceCollector collector) =>
        {
            var traces = collector.GetRecentTraces(count ?? 10);
            return Results.Json(new
            {
                count = traces.Count,
                traces = traces.Select(t => new
                {
                    t.TraceId, t.Verdict,
                    query = t.UserQuery[..Math.Min(t.UserQuery.Length, 100)],
                    t.OverallConfidence, t.TotalTokens,
                    step_count = t.Steps.Count,
                    duration_ms = (t.CompletedAt - t.StartedAt).TotalMilliseconds
                })
            });
        });

        endpoints.MapGet("/api/trace/stats", (TraceCollector collector) =>
            Results.Json(collector.GetStats()));
    }

    private static object BuildPlanJson(PlanNode node)
    {
        return new
        {
            node.Id, type = node.Type.ToString(), node.Name,
            node.Description,
            tool_calls = node.ToolCalls,
            children = node.Children.Select(BuildPlanJson)
        };
    }
}
