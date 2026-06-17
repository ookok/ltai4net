using LTAI.Agent.Vector;
using Xunit;

namespace LTAI.Tests;

public class ContractRegistryTests
{
    private readonly ContractRegistry _registry = new();

    [Fact]
    public void Register_AddsContract()
    {
        _registry.Register("repo-a", "api.cs", ContractType.Http, "GET::/users", "provider");
        Assert.Equal(1, _registry.Count);
    }

    [Fact]
    public void FindCrossRepo_DetectsSharedContract()
    {
        _registry.Register("repo-a", "api.cs", ContractType.Http, "GET::/users", "provider");
        _registry.Register("repo-b", "client.cs", ContractType.Http, "GET::/users", "consumer");

        var shared = _registry.FindCrossRepo("repo-a", "repo-b");
        Assert.Single(shared);
        Assert.Equal("GET::/users", shared[0].Contract.Id);
    }

    [Fact]
    public void FindCrossRepo_Empty_WhenNoMatch()
    {
        _registry.Register("repo-a", "api.cs", ContractType.Http, "GET::/users", "provider");
        _registry.Register("repo-b", "client.cs", ContractType.Http, "POST::/items", "consumer");

        var shared = _registry.FindCrossRepo("repo-a", "repo-b");
        Assert.Empty(shared);
    }

    [Fact]
    public void FindOrphans_DetectsUnmatchedContracts()
    {
        _registry.Register("repo-a", "api.cs", ContractType.Http, "GET::/orphan", "provider");
        _registry.Register("repo-b", "client.cs", ContractType.Http, "GET::/matched", "provider");
        _registry.Register("repo-c", "consumer.cs", ContractType.Http, "GET::/matched", "consumer");

        var orphans = _registry.FindOrphans();
        Assert.Contains(orphans, o => o.Key == "Http::GET::/orphan");
        Assert.DoesNotContain(orphans, o => o.Key == "Http::GET::/matched");
    }

    [Fact]
    public void ScanFile_DetectsHttpRoutes()
    {
        var code = """
            app.MapGet("/api/users", handler);
            app.MapPost("/api/items", handler);
            """;

        _registry.ScanFile("repo", "api.cs", code);
        Assert.Equal(2, _registry.Count);
    }

    [Fact]
    public void ScanFile_DetectsEnvVars()
    {
        var code = """
            var db = os.Getenv("DATABASE_URL");
            var key = Environment.GetEnvironmentVariable("API_KEY");
            """;

        _registry.ScanFile("repo", "config.cs", code);
        Assert.True(_registry.Count >= 2);
    }

    [Fact]
    public void Clear_ResetsAll()
    {
        _registry.Register("repo-a", "api.cs", ContractType.Http, "GET::/users", "provider");
        _registry.Clear();
        Assert.Equal(0, _registry.Count);
        Assert.Equal("No contracts registered.", _registry.ToString());
    }
}
