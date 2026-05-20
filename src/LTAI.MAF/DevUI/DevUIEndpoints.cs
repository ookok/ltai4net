using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LTAI.MAF;

public static class DevUIEndpoints
{
    public static void MapDevUIEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/devui", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(DevUIHtml.Page);
        });

        endpoints.MapGet("/api/devui/state", async (HttpContext context) =>
        {
            var state = new
            {
                session = new { id = Guid.NewGuid().ToString("N")[..8], started_at = DateTime.UtcNow.ToString("o"), total_tokens = 1250, total_cost = 0.0035 },
                agents = new[]
                {
                    new { name = "Evolver", role = "Generates solutions", status = "idle", calls = 42, avg_latency_ms = 320, tokens = 580 },
                    new { name = "Evaluator", role = "Evaluates quality", status = "idle", calls = 38, avg_latency_ms = 180, tokens = 420 },
                    new { name = "Verifier", role = "Verifies correctness", status = "idle", calls = 35, avg_latency_ms = 150, tokens = 250 }
                },
                workflows = WorkflowRegistry.GetAll(),
                governance = Governance.ActionGovernor.Instance.GetStats(),
                storage = Hosting.ChatHistoryManager.Instance.DescribeBackends(),
                graph = new
                {
                    nodes = new[]
                    {
                        new { id = "input", label = "User Input", group = "io" },
                        new { id = "governor", label = "Governor (AGT)", group = "pipeline" },
                        new { id = "moe", label = "ContextMoE (5-tier)", group = "memory" },
                        new { id = "codeact", label = "CodeAct (Hyperlight)", group = "tool" },
                        new { id = "skills", label = "Skills (5 builtin)", group = "tool" },
                        new { id = "workflow", label = "Workflow Engine", group = "orchestra" },
                        new { id = "agui", label = "AG-UI Stream", group = "io" },
                        new { id = "output", label = "Response", group = "io" }
                    },
                    edges = new[]
                    {
                        new { from = "input", to = "governor" },
                        new { from = "governor", to = "moe" },
                        new { from = "moe", to = "codeact" },
                        new { from = "moe", to = "skills" },
                        new { from = "codeact", to = "workflow" },
                        new { from = "skills", to = "workflow" },
                        new { from = "workflow", to = "agui" },
                        new { from = "agui", to = "output" }
                    }
                }
            };

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        });

        endpoints.MapGet("/api/devui/agui-stream", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";

            var hub = AGUI.AgUiStreamHub.Instance;
            var tcs = new TaskCompletionSource<bool>();
            context.RequestAborted.Register(() => tcs.TrySetResult(true));

            hub.Subscribe(async evt =>
            {
                try
                {
                    var sse = hub.RenderSseEvent(evt);
                    await context.Response.WriteAsync(sse, context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
                catch { }
            });

            await tcs.Task;
        });
    }
}

public static class WorkflowRegistry
{
    private static readonly List<object> _workflows = new();
    private static readonly object _lock = new();

    public static void Record(string id, string type, int steps, string status, long latencyMs)
    {
        lock (_lock)
        {
            _workflows.Add(new { id = id, type = type, steps = steps, status = status, latencyMs = latencyMs, ts = DateTime.UtcNow.ToString("HH:mm:ss") });
            if (_workflows.Count > 50) _workflows.RemoveAt(0);
        }
    }

    public static List<object> GetAll()
    {
        lock (_lock) { return _workflows.ToList(); }
    }
}
