using System.Text.Json;
using LTAI.Agent.Federation;
using LTAI.Knowledge.Memory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LTAI.Web;

public static class InnovationEndpoints
{
    public static void MapInnovationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // ─── Temporal Memory Fabric ───
        endpoints.MapPost("/api/memory/query", async (HttpContext context, TemporalMemoryFabric memory) =>
        {
            var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
            var request = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);

            if (request == null || !request.TryGetValue("query", out var q))
                return Results.Json(new { error = "query is required" }, statusCode: 400);

            var query = q.GetString() ?? "";
            var results = await memory.QueryAsync(query, timeWindow: null, filePath: null, topK: 10);

            return Results.Json(new
            {
                query,
                count = results.Count,
                results = results.Select(r => new
                {
                    r.Id, r.Source, r.Score,
                    content = r.Content[..Math.Min(r.Content.Length, 500)],
                    r.Timestamp, r.FilePath, r.GraphTriplet
                })
            });
        });

        endpoints.MapPost("/api/memory/record", (HttpContext context, TemporalMemoryFabric memory) =>
        {
            var body = new StreamReader(context.Request.Body).ReadToEnd();
            var request = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);

            if (request == null || !request.TryGetValue("session_id", out var sid))
                return Results.Json(new { error = "session_id is required" }, statusCode: 400);

            var evt = new MemoryEvent
            {
                SessionId = sid.GetString() ?? "anon",
                AgentName = request.TryGetValue("agent", out var a) ? a.GetString() ?? "" : "",
                UserQuery = request.TryGetValue("query", out var q) ? q.GetString() ?? "" : "",
                AgentResponse = request.TryGetValue("response", out var r) ? r.GetString() : null,
                FilePath = request.TryGetValue("file_path", out var f) ? f.GetString() : null,
                Importance = request.TryGetValue("importance", out var i) && i.ValueKind == JsonValueKind.Number
                    ? i.GetDouble() : 0.5,
                Metadata = request.TryGetValue("metadata", out var m) && m.ValueKind == JsonValueKind.Object
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(m.GetRawText()) ?? new()
                    : new()
            };

            memory.RecordEvent(evt);
            return Results.Json(new { event_id = evt.Id, status = "recorded" });
        });

        endpoints.MapGet("/api/memory/session/{sessionId}", (string sessionId, TemporalMemoryFabric memory) =>
        {
            var history = memory.GetSessionHistory(sessionId);
            return Results.Json(new
            {
                session_id = sessionId,
                count = history.Count,
                events = history.Select(e => new
                {
                    e.Id, e.AgentName,
                    query = e.UserQuery[..Math.Min(e.UserQuery.Length, 200)],
                    e.FilePath, e.Importance, e.Timestamp
                })
            });
        });

        endpoints.MapGet("/api/memory/stats", (TemporalMemoryFabric memory) =>
            Results.Json(memory.GetStats()));

        // ─── Federated Agent Mesh ───
        endpoints.MapGet("/api/federation/nodes", (FederationCoordinator federation) =>
        {
            var nodes = federation.DiscoverNodes();
            return Results.Json(new
            {
                local_node_id = federation.LocalNodeId,
                count = nodes.Count,
                nodes = nodes.Select(n => new
                {
                    n.NodeId, n.Address,
                    capabilities = n.Capabilities.Select(c => c.ToString()),
                    n.CurrentLoad, n.MaxConcurrency,
                    n.ReliabilityScore, n.LatencyMs
                })
            });
        });

        endpoints.MapPost("/api/federation/register", (HttpContext context, FederationCoordinator federation) =>
        {
            var body = new StreamReader(context.Request.Body).ReadToEnd();
            var request = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);

            if (request == null)
                return Results.Json(new { error = "Invalid request" }, statusCode: 400);

            var caps = request.TryGetValue("capabilities", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray().Select(e => Enum.Parse<NodeCapability>(e.GetString() ?? "Chat")).ToList()
                : new List<NodeCapability>();

            var node = new FederationNode
            {
                NodeId = request.TryGetValue("node_id", out var nid) ? nid.GetString() ?? "" : Guid.NewGuid().ToString("N")[..8],
                PeerId = request.TryGetValue("peer_id", out var pid) ? pid.GetString() ?? "" : "",
                Address = request.TryGetValue("address", out var addr) ? addr.GetString() ?? "" : "",
                Capabilities = caps,
                MaxConcurrency = request.TryGetValue("max_concurrency", out var mc) && mc.TryGetInt32(out var mci) ? mci : 5,
                ReliabilityScore = request.TryGetValue("reliability", out var rel) && rel.TryGetDouble(out var reld) ? reld : 1.0
            };

            federation.RegisterRemoteNode(node);
            return Results.Json(new { status = "registered", node_id = node.NodeId });
        });

        endpoints.MapPost("/api/federation/dispatch", async (HttpContext context, FederationCoordinator federation) =>
        {
            var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
            var request = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);

            if (request == null || !request.TryGetValue("query", out var q))
                return Results.Json(new { error = "query is required" }, statusCode: 400);

            var capability = request.TryGetValue("capability", out var cap)
                ? Enum.Parse<NodeCapability>(cap.GetString() ?? "Chat")
                : NodeCapability.Chat;

            var task = await federation.DispatchAsync(q.GetString() ?? "", capability);
            return Results.Json(new
            {
                task_id = task.TaskId,
                status = task.Status,
                target_node = task.TargetNodeId,
                capability = task.RequiredCapability.ToString()
            });
        });

        endpoints.MapPost("/api/federation/complete", (HttpContext context, FederationCoordinator federation) =>
        {
            var body = new StreamReader(context.Request.Body).ReadToEnd();
            var request = JsonSerializer.Deserialize<Dictionary<string, string>>(body);

            if (request == null || !request.TryGetValue("task_id", out var taskId))
                return Results.Json(new { error = "task_id is required" }, statusCode: 400);

            var response = request.GetValueOrDefault("response", "");
            var success = request.GetValueOrDefault("status", "completed") == "completed";

            federation.CompleteTask(taskId, response, success);
            return Results.Json(new { status = "completed" });
        });

        endpoints.MapGet("/api/federation/stats", (FederationCoordinator federation) =>
            Results.Json(federation.GetStats()));
    }
}
