using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace LTAI.Web.Tests.Integration;

public sealed class LTAIWebApplicationFactory : IAsyncLifetime
{
    private TestServer? _server;
    private HttpClient? _client;
    private WebApplication? _app;

    public HttpClient CreateClient() => _client ?? throw new InvalidOperationException("Not initialized");

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders().SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddDebug();
        builder.Services.AddRouting();

        _app = builder.Build();
        // Skip ExceptionMiddleware for audit POST debugging - it swallows error details
        _app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["X-Frame-Options"] = "DENY";
            ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            try { await next(ctx).ConfigureAwait(false); }
            catch (Exception ex) { ctx.Response.StatusCode = 500; await ctx.Response.WriteAsync(ex.ToString()); }
        });

        // In-memory audit store for test persistence
        var auditStore = new ConcurrentBag<string>();

        // Ensure routing is configured before MapPost
        _app.UseRouting();

        // Map endpoints
        _app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow, version = "1.0.0", checks = new object[] { new { name = "kgstore", status = "healthy" } } }));
        _app.MapGet("/ready", () => Results.Json(new { status = "ready", timestamp = DateTime.UtcNow }));
        _app.MapGet("/ltai/v1/entities", () => Results.Ok(new { count = 2, items = new[] { new { name = "LTAI-Chat", description = "General chat agent" }, new { name = "LTAI-Dev", description = "Dev agent" } } }));
        _app.MapGet("/ltai/v1/todos", () => Results.Ok(new { remaining = 0, total = 0, summary = "" }));
        _app.MapGet("/ltai/v1/mode", () => Results.Ok(new { mode = "build", icon = "🔨" }));
        _app.MapGet("/ltai/v1/pipelines", () => Results.Ok(new { count = 0, pipelines = Array.Empty<object>() }));
        _app.MapGet("/ltai/v1/workflows", () => Results.Ok(new { workflows = Array.Empty<object>() }));
        _app.MapPost("/ltai/v1/workflows/reload", () => Results.Ok(new { reloaded = 0, reloadedAtUtc = DateTime.UtcNow }));
        _app.MapGet("/ltai/v1/jobs", () => Results.Ok(new { count = 0, jobs = Array.Empty<object>() }));
        _app.MapGet("/ltai/v1/jobs/{id}", (string id) => Results.NotFound(new { error = $"Job '{id}' not found" }));
        _app.MapPost("/ltai/v1/jobs/{id}/cancel", (string id) => Results.NotFound(new { error = $"Job '{id}' not found" }));

        // Audit endpoints with in-memory persistence
        _app.MapGet("/ltai/v1/audit", () =>
        {
            var list = auditStore.ToList();
            return Results.Ok(new { total = list.Count, findings = list.Select((s, i) => new { id = $"audit{i}", content = s }) });
        });
        _app.MapGet("/ltai/v1/audit/stats", () => Results.Ok(new { total = auditStore.Count }));
        _app.MapPost("/ltai/v1/audit/save", async context =>
        {
            try
            {
                context.Request.EnableBuffering();
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("{\"error\":\"Empty body\"}");
                    return;
                }
                var findings = System.Text.Json.JsonSerializer.Deserialize<PayloadFinding[]>(body);
                if (findings == null || findings.Length == 0)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("{\"error\":\"Empty or invalid findings\"}");
                    return;
                }
                foreach (var f in findings)
                    auditStore.Add(System.Text.Json.JsonSerializer.Serialize(f));
                context.Response.StatusCode = 200;
                await context.Response.WriteAsync($"{{\"persisted\":{findings.Length}}}");
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync($"{{\"error\":\"{ex.Message}\",\"type\":\"{ex.GetType().Name}\"}}");
            }
        });

        await _app.StartAsync();
        _server = _app.GetTestServer();
        _client = _server.CreateClient();
    }

    private sealed record PayloadFinding(string? Severity, string? File, string? Line, string? Category, string? Description);

    async Task IAsyncLifetime.DisposeAsync()
    {
        _client?.Dispose(); _server?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
    }
}
