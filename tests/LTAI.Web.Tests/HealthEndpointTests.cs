using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace LTAI.Web.Tests;

public sealed class HealthEndpointTests : IClassFixture<WebTestsFactory>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(WebTestsFactory factory)
    {
        _client = factory.Client;
    }

    [Fact]
    public async Task Health_Returns200()
    {
        var resp = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Ready_Returns200()
    {
        var resp = await _client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task Classify_WithoutDevelopment_Returns404()
    {
        // Test stub endpoints don't register /ltai/v1/classify
        var resp = await _client.GetAsync("/ltai/v1/classify?query=hello");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Entities_ReturnsAgentList()
    {
        var resp = await _client.GetAsync("/ltai/v1/entities");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("count", out var count) && count.GetInt32() > 0);
        Assert.True(body.TryGetProperty("items", out _));
    }

    [Fact]
    public async Task Todos_ReturnsData()
    {
        var resp = await _client.GetAsync("/ltai/v1/todos");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Mode_ReturnsCurrentMode()
    {
        var resp = await _client.GetAsync("/ltai/v1/mode");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Pipelines_ReturnsList()
    {
        var resp = await _client.GetAsync("/ltai/v1/pipelines");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Workflows_ReturnsList()
    {
        var resp = await _client.GetAsync("/ltai/v1/workflows");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task WorkflowReload_ReturnsOk()
    {
        var resp = await _client.PostAsync("/ltai/v1/workflows/reload", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Jobs_ReturnsSnapshot()
    {
        var resp = await _client.GetAsync("/ltai/v1/jobs");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Audit_ReturnsFindings()
    {
        var resp = await _client.GetAsync("/ltai/v1/audit");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("total", out _));
        Assert.True(body.TryGetProperty("findings", out _));
    }

    [Fact]
    public async Task AuditStats_ReturnsOk()
    {
        var resp = await _client.GetAsync("/ltai/v1/audit/stats");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task AuditSave_Post_ReturnsOk()
    {
        var content = JsonContent.Create(new
        {
            findings = new[]
            {
                new { Severity = "P0", File = "test.cs", Line = "1", Category = "security", Description = "test finding" }
            }
        });
        var resp = await _client.PostAsync("/ltai/v1/audit/save", content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("persisted").GetInt32() >= 1);
    }
}
