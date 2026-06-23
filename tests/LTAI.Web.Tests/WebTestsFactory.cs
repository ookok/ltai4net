using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LTAI.Web.Tests;

/// <summary>Test stub server matching real LTAI.Web endpoint shapes.
/// Uses Map+Run (available on IApplicationBuilder) instead of MapGet.</summary>
public sealed class WebTestsFactory : IDisposable
{
    private readonly TestServer _server;

    public HttpClient Client { get; }

    public WebTestsFactory()
    {
        var builder = new WebHostBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Warning);
            })
            .UseEnvironment("Development")
            .ConfigureServices(services =>
            {
                services.AddRouting();
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGet("/health", () => Results.Ok(new
                    {
                        status = "healthy",
                        timestamp = DateTime.UtcNow,
                        version = "1.0.0",
                        checks = new object[]
                        {
                            new { name = "kg_store", status = "healthy" },
                            new { name = "llm_providers", status = "healthy", count = 0, providers = Array.Empty<string>() }
                        }
                    }));

                    endpoints.MapGet("/ready", () => Results.Ok(new { ready = true, timestamp = DateTime.UtcNow }));

                    endpoints.MapGet("/ltai/v1/entities", () => Results.Ok(new
                    {
                        count = 19,
                        items = new[]
                        {
                            new { name = "LTAI-Chat", description = "General chat agent" },
                            new { name = "LTAI-Dev", description = "Dev agent" }
                        }
                    }));

                    endpoints.MapGet("/ltai/v1/todos", () => Results.Ok(new { remaining = 0, total = 0, summary = "" }));
                    endpoints.MapGet("/ltai/v1/mode", () => Results.Ok(new { mode = "build", icon = "🔨" }));

                    endpoints.MapGet("/ltai/v1/pipelines", () => Results.Ok(new
                    {
                        count = 1,
                        items = new[] { new { name = "default", steps = new[] { "GrammarCheck", "QualityGate", "DoDCheck", "Retrospective" } } }
                    }));

                    endpoints.MapGet("/ltai/v1/workflows", () => Results.Ok(Array.Empty<object>()));
                    endpoints.MapPost("/ltai/v1/workflows/reload", () => Results.Ok(new { reloaded = true }));

                    endpoints.MapGet("/ltai/v1/jobs", () => Results.Ok(new { count = 0, items = new List<object>() }));

                    endpoints.MapGet("/ltai/v1/audit", () => Results.Ok(new { total = 0, findings = new List<object>() }));
                    endpoints.MapGet("/ltai/v1/audit/stats", () => Results.Ok(new { total = 0 }));
                    endpoints.MapPost("/ltai/v1/audit/save", async (HttpContext ctx) =>
                    {
                        var body = await System.Text.Json.JsonSerializer.DeserializeAsync<System.Text.Json.JsonElement>(
                            ctx.Request.Body, cancellationToken: ctx.RequestAborted);
                        var persisted = body.TryGetProperty("findings", out var f) ? f.GetArrayLength() : 0;
                        return Results.Ok(new { persisted });
                    });
                });
            });

        _server = new TestServer(builder);
        Client = _server.CreateClient();
    }

    public void Dispose()
    {
        Client.Dispose();
        _server.Dispose();
    }
}
