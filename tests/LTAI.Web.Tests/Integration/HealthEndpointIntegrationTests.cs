using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace LTAI.Web.Tests.Integration;

public sealed class HealthEndpointIntegrationTests : IClassFixture<LTAIWebApplicationFactory>
{
    private readonly HttpClient _client;

    public HealthEndpointIntegrationTests(LTAIWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
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
    public async Task Health_HasRequiredChecks()
    {
        var resp = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("checks", out var checks));
    }

    [Fact]
    public async Task Ready_Returns200()
    {
        var resp = await _client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ready", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Entities_ReturnsAgentList()
    {
        var resp = await _client.GetAsync("/ltai/v1/entities");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("count", out var count) && count.GetInt32() > 0);
        Assert.True(body.TryGetProperty("items", out var items) && items.GetArrayLength() > 0);
    }

    [Fact]
    public async Task Todos_ReturnsData()
    {
        var resp = await _client.GetAsync("/ltai/v1/todos");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("remaining", out _));
        Assert.True(body.TryGetProperty("total", out _));
    }

    [Fact]
    public async Task Mode_ReturnsCurrentMode()
    {
        var resp = await _client.GetAsync("/ltai/v1/mode");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("mode", out _));
        Assert.True(body.TryGetProperty("icon", out _));
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
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("count", out _));
        Assert.True(body.TryGetProperty("jobs", out _));
    }
}
