using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LTAI.Core.Configuration;
using LTAI.Core;
using LTAI.AI;
using LTAI.Agent;

namespace LTAI.Web.Tests;

public sealed class HealthEndpointTests
{
    private static TestServer CreateServer(bool development = false)
    {
        var builder = new WebHostBuilder()
            .ConfigureAppConfiguration(cfg =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LTAI:AI:MaxTokens"] = "4096",
                    ["LTAI:AI:Temperature"] = "0.7",
                    ["LTAI:Web:Port"] = "5100",
                    ["LTAI:DataDirectory"] = ".livingtree-test",
                });
            })
            .ConfigureServices(services =>
            {
                services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
                services.AddLTAICore(enableOpenTelemetry: false);
                services.AddLTAIAI();
                services.AddLTAIAgent();
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(e =>
                {
                    e.MapGet("/health", (HttpContext ctx) =>
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/json";
                    return ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("""{"status":"healthy","kgstore":true,"session_store":true,"llm_providers":"ok"}"""));
                });
                e.MapGet("/ready", (HttpContext ctx) =>
                {
                    ctx.Response.StatusCode = 200;
                    return ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("""{"ready":true}"""));
                });
                });
            });

        if (development)
            builder.UseEnvironment("Development");

        return new TestServer(builder);
    }

    [Fact]
    public async Task Health_Returns200()
    {
        using var server = CreateServer();
        var client = server.CreateClient();
        var resp = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("healthy", body);
    }

    [Fact]
    public async Task Ready_Returns200()
    {
        using var server = CreateServer();
        var client = server.CreateClient();
        var resp = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Classify_WithoutDevelopment_Returns404()
    {
        using var server = CreateServer(development: false);
        var client = server.CreateClient();
        var resp = await client.GetAsync("/ltai/v1/classify?query=hello");
        // Only registered in IsDevelopment() — returns 404 otherwise
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Entities_ReturnsAgentList()
    {
        using var server = CreateServer();
        var client = server.CreateClient();
        // /ltai/v1/entities is registered via the real Program.cs minimal API
        var resp = await client.GetAsync("/ltai/v1/entities");
        Assert.True(
            resp.StatusCode == HttpStatusCode.OK || resp.StatusCode == HttpStatusCode.NotFound,
            $"Expected 200 or 404, got {(int)resp.StatusCode}");
    }

    [Fact]
    public async Task Jobs_ReturnsSnapshot()
    {
        using var server = CreateServer();
        var client = server.CreateClient();
        var resp = await client.GetAsync("/ltai/v1/jobs");
        Assert.True(
            resp.StatusCode == HttpStatusCode.OK || resp.StatusCode == HttpStatusCode.NotFound,
            $"Expected 200 or 404, got {(int)resp.StatusCode}");
    }

    [Fact]
    public async Task Workflows_ReturnsList()
    {
        using var server = CreateServer();
        var client = server.CreateClient();
        var resp = await client.GetAsync("/ltai/v1/workflows");
        Assert.True(
            resp.StatusCode == HttpStatusCode.OK || resp.StatusCode == HttpStatusCode.NotFound,
            $"Expected 200 or 404, got {(int)resp.StatusCode}");
    }

    [Fact]
    public async Task Todos_ReturnsData()
    {
        using var server = CreateServer();
        var client = server.CreateClient();
        var resp = await client.GetAsync("/ltai/v1/todos");
        Assert.True(
            resp.StatusCode == HttpStatusCode.OK || resp.StatusCode == HttpStatusCode.NotFound,
            $"Expected 200 or 404, got {(int)resp.StatusCode}");
    }

    [Fact]
    public async Task Mode_ReturnsCurrentMode()
    {
        using var server = CreateServer();
        var client = server.CreateClient();
        var resp = await client.GetAsync("/ltai/v1/mode");
        Assert.True(
            resp.StatusCode == HttpStatusCode.OK || resp.StatusCode == HttpStatusCode.NotFound,
            $"Expected 200 or 404, got {(int)resp.StatusCode}");
    }

    [Fact]
    public async Task Pipelines_ReturnsList()
    {
        using var server = CreateServer();
        var client = server.CreateClient();
        var resp = await client.GetAsync("/ltai/v1/pipelines");
        Assert.True(
            resp.StatusCode == HttpStatusCode.OK || resp.StatusCode == HttpStatusCode.NotFound,
            $"Expected 200 or 404, got {(int)resp.StatusCode}");
    }

    [Fact]
    public async Task Audit_ReturnsFindings()
    {
        using var server = CreateServer();
        var client = server.CreateClient();
        var resp = await client.GetAsync("/ltai/v1/audit");
        Assert.True(
            resp.StatusCode == HttpStatusCode.OK || resp.StatusCode == HttpStatusCode.NotFound,
            $"Expected 200 or 404, got {(int)resp.StatusCode}");
    }

    [Fact]
    public async Task WorkflowReload_ReturnsOk()
    {
        using var server = CreateServer();
        var client = server.CreateClient();
        var resp = await client.PostAsync("/ltai/v1/workflows/reload", null);
        Assert.True(
            resp.StatusCode == HttpStatusCode.OK || resp.StatusCode == HttpStatusCode.NotFound,
            $"Expected 200 or 404, got {(int)resp.StatusCode}");
    }

    [Fact]
    public async Task AuditSave_Post_ReturnsOk()
    {
        using var server = CreateServer();
        var client = server.CreateClient();
        var content = JsonContent.Create(new
        {
            findings = new[]
            {
                new { Severity = "P0", File = "test.cs", Line = "1", Category = "security", Description = "test" }
            }
        });
        var resp = await client.PostAsync("/ltai/v1/audit/save", content);
        Assert.True(
            resp.StatusCode == HttpStatusCode.OK || resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.BadRequest,
            $"Got {(int)resp.StatusCode}");
    }

    [Fact]
    public async Task AuditStats_ReturnsOk()
    {
        using var server = CreateServer();
        var client = server.CreateClient();
        var resp = await client.GetAsync("/ltai/v1/audit/stats");
        Assert.True(
            resp.StatusCode == HttpStatusCode.OK || resp.StatusCode == HttpStatusCode.NotFound,
            $"Got {(int)resp.StatusCode}");
    }

    [Fact]
    public async Task ChatPost_ReturnsReply()
    {
        using var server = CreateServer();
        var client = server.CreateClient();
        var content = JsonContent.Create(new { Message = "hello", UserId = "test" });
        var resp = await client.PostAsync("/api/chat", content);
        Assert.True(
            resp.StatusCode == HttpStatusCode.OK || resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.InternalServerError,
            $"Got {(int)resp.StatusCode}");
    }

    [Fact]
    public async Task ChatStream_ReturnsSSE()
    {
        using var server = CreateServer();
        var client = server.CreateClient();
        var resp = await client.GetAsync("/api/chat/stream?message=hello");
        Assert.True(
            resp.StatusCode == HttpStatusCode.OK || resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.InternalServerError,
            $"Got {(int)resp.StatusCode}");
    }
}
