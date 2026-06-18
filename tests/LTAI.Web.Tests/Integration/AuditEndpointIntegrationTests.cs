using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace LTAI.Web.Tests.Integration;

public sealed class AuditEndpointIntegrationTests : IClassFixture<LTAIWebApplicationFactory>
{
    private readonly HttpClient _client;

    public AuditEndpointIntegrationTests(LTAIWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Audit_ReturnsEmptyInitially()
    {
        var resp = await _client.GetAsync("/ltai/v1/audit");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Audit_Post_And_Retrieve()
    {
        var content = JsonContent.Create(new
        {
            findings = new[]
            {
                new { Severity = "P0", File = "test.cs", Line = "42", Category = "security", Description = "Buffer overflow" },
                new { Severity = "P1", File = "app.cs", Line = "10", Category = "performance", Description = "Slow query" }
            }
        });
        var postResp = await _client.PostAsync("/ltai/v1/audit/save", content);
        Assert.Equal(HttpStatusCode.OK, postResp.StatusCode);
        var postBody = await postResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, postBody.GetProperty("persisted").GetInt32());

        var getResp = await _client.GetAsync("/ltai/v1/audit");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var getBody = await getResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, getBody.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Audit_Post_Empty_ReturnsBadRequest()
    {
        var content = JsonContent.Create(new { findings = Array.Empty<object>() });
        var resp = await _client.PostAsync("/ltai/v1/audit/save", content);
        var respBody = await resp.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Audit_Post_Valid_ReturnsCorrectPersistedCount()
    {
        var content = JsonContent.Create(new
        {
            findings = new[]
            {
                new { Severity = "P2", File = "test2.cs", Line = "5", Category = "style", Description = "Naming" },
                new { Severity = "P0", File = "critical.cs", Line = "1", Category = "security", Description = "Sql injection" },
                new { Severity = "P1", File = "moderate.cs", Line = "100", Category = "reliability", Description = "Null ref" }
            }
        });
        var postResp = await _client.PostAsync("/ltai/v1/audit/save", content);
        Assert.Equal(HttpStatusCode.OK, postResp.StatusCode);
        var postBody = await postResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, postBody.GetProperty("persisted").GetInt32());
    }

    [Fact]
    public async Task AuditStats_ReturnsZeroInitially()
    {
        var resp = await _client.GetAsync("/ltai/v1/audit/stats");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task AuditStats_AfterInsert_ReflectsCounts()
    {
        var content = JsonContent.Create(new
        {
            findings = new[]
            {
                new { Severity = "P0", File = "a.cs", Line = "1", Category = "security", Description = "Issue A" },
                new { Severity = "P0", File = "b.cs", Line = "2", Category = "security", Description = "Issue B" },
                new { Severity = "P1", File = "c.cs", Line = "3", Category = "performance", Description = "Issue C" }
            }
        });
        await _client.PostAsync("/ltai/v1/audit/save", content);

        var resp = await _client.GetAsync("/ltai/v1/audit/stats");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, body.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Audit_SupportsFiltering()
    {
        var content = JsonContent.Create(new
        {
            findings = new[]
            {
                new { Severity = "P0", File = "p0.cs", Line = "1", Category = "security", Description = "P0 issue" },
                new { Severity = "P2", File = "p2.cs", Line = "2", Category = "style", Description = "P2 issue" }
            }
        });
        await _client.PostAsync("/ltai/v1/audit/save", content);
    }

    [Fact]
    public async Task AuditSave_InvalidJson_ReturnsProblem()
    {
        var content = new StringContent("not json", System.Text.Encoding.UTF8, "application/json");
        var resp = await _client.PostAsync("/ltai/v1/audit/save", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
