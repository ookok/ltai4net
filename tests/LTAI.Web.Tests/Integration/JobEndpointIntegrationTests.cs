using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace LTAI.Web.Tests.Integration;

public sealed class JobEndpointIntegrationTests : IClassFixture<LTAIWebApplicationFactory>
{
    private readonly HttpClient _client;

    public JobEndpointIntegrationTests(LTAIWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Jobs_List_ReturnsEmptyInitially()
    {
        var resp = await _client.GetAsync("/ltai/v1/jobs");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("count").GetInt32());
        Assert.Empty(body.GetProperty("jobs").EnumerateArray());
    }

    [Fact]
    public async Task Jobs_GetById_NotFound_Returns404()
    {
        var resp = await _client.GetAsync("/ltai/v1/jobs/nonexistent");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Jobs_Cancel_NonExistent_Returns404()
    {
        var resp = await _client.PostAsync("/ltai/v1/jobs/nonexistent/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Jobs_ListShape_IsCorrect()
    {
        var resp = await _client.GetAsync("/ltai/v1/jobs");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("count", out _));
        Assert.True(body.TryGetProperty("jobs", out var jobs));
        Assert.Equal(JsonValueKind.Array, jobs.ValueKind);
    }
}
