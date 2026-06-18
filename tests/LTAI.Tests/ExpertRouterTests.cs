using LTAI.AI;
using LTAI.Agent.Experts;
using LTAI.Agent.Experts.Routing;
using Xunit;

namespace LTAI.Tests;

public sealed class ExpertRouterTests
{
    private static EmbeddingClient CreateEmbedder() => new();

    [Fact]
    public async Task SelectExpertsAsync_NoExperts_ReturnsEmpty()
    {
        var registry = new ExpertRegistry([], CreateEmbedder());
        var router = new ExpertRouter(registry);
        var result = await router.SelectExpertsAsync("test query");
        Assert.Empty(result.Selections);
        Assert.Contains("No experts", result.Reasoning);
    }

    [Fact]
    public async Task ExpertRegistry_NoEmbedding_FallbackToFastEmb()
    {
        var experts = new IExpertModule[] { new TestExpert("ex1", "test expert", ExpertDomain.KG) };
        var registry = new ExpertRegistry(experts, CreateEmbedder());
        await registry.EnsureEmbeddingsAsync();
        Assert.NotNull(registry.Entries[0].Embedding);
        Assert.Equal(384, registry.Entries[0].Embedding!.Length);
    }

    [Fact]
    public async Task SelectTopKAsync_Experts_ReturnsScoredResults()
    {
        var experts = new IExpertModule[]
        {
            new TestExpert("code", "C# code analysis and refactoring", ExpertDomain.CodeGraph),
            new TestExpert("doc", "technical documentation writing", ExpertDomain.Document),
        };
        var registry = new ExpertRegistry(experts, CreateEmbedder());
        await registry.EnsureEmbeddingsAsync();
        var results = await registry.SelectTopKAsync("refactoring C# methods", k: 2);
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Score >= 0));
    }

    [Fact]
    public async Task SelectTopKAsync_EmptyQuery_ReturnsAll()
    {
        var experts = new IExpertModule[]
        {
            new TestExpert("a", "alpha", ExpertDomain.KG),
            new TestExpert("b", "beta", ExpertDomain.Tool),
        };
        var registry = new ExpertRegistry(experts, CreateEmbedder());
        await registry.EnsureEmbeddingsAsync();
        var results = await registry.SelectTopKAsync("", k: 5);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SelectExpertsAsync_EmptyRegistry_ReturnsEmpty()
    {
        var registry = new ExpertRegistry([], CreateEmbedder());
        var router = new ExpertRouter(registry);
        var result = await router.SelectExpertsAsync("anything");
        Assert.Empty(result.Selections);
    }

    [Fact]
    public async Task EnsureEmbeddingsAsync_MultipleCalls_Idempotent()
    {
        var experts = new IExpertModule[] { new TestExpert("x", "test", ExpertDomain.KG) };
        var registry = new ExpertRegistry(experts, CreateEmbedder());
        await registry.EnsureEmbeddingsAsync();
        var firstVec = registry.Entries[0].Embedding;
        await registry.EnsureEmbeddingsAsync();
        var secondVec = registry.Entries[0].Embedding;
        Assert.Equal(firstVec, secondVec);
    }

    [Fact]
    public void Constructor_EmptyExperts_NoThrow()
    {
        var registry = new ExpertRegistry([], CreateEmbedder());
        Assert.Empty(registry.Entries);
    }

    [Fact]
    public void ExpertEntry_StoresDescription()
    {
        var expert = new TestExpert("t1", "custom description", ExpertDomain.Tool);
        var registry = new ExpertRegistry([expert], CreateEmbedder());
        var entry = Assert.Single(registry.Entries);
        Assert.Equal("custom description", entry.CapabilityText);
    }

    [Fact]
    public async Task SelectTopKAsync_TopK_LimitsResults()
    {
        var experts = new IExpertModule[]
        {
            new TestExpert("a", "topic alpha", ExpertDomain.KG),
            new TestExpert("b", "topic beta", ExpertDomain.CodeGraph),
            new TestExpert("c", "topic gamma", ExpertDomain.Document),
            new TestExpert("d", "topic delta", ExpertDomain.Tool),
        };
        var registry = new ExpertRegistry(experts, CreateEmbedder());
        await registry.EnsureEmbeddingsAsync();
        var results = await registry.SelectTopKAsync("topic", k: 2);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SelectTopKAsync_RelevanceOrdered()
    {
        var experts = new IExpertModule[]
        {
            new TestExpert("irrelevant", "music theory and piano composition", ExpertDomain.Skill),
            new TestExpert("relevant", "machine learning model training in PyTorch", ExpertDomain.KG),
        };
        var registry = new ExpertRegistry(experts, CreateEmbedder());
        await registry.EnsureEmbeddingsAsync();
        var results = await registry.SelectTopKAsync("training neural networks with PyTorch");
        Assert.NotEmpty(results);
        Assert.Equal("relevant", results[0].Expert.ExpertId);
    }

    [Fact]
    public async Task Router_WithSingleExpert_ReturnsIt()
    {
        var expert = new TestExpert("only", "sole expert", ExpertDomain.KG);
        var registry = new ExpertRegistry([expert], CreateEmbedder());
        await registry.EnsureEmbeddingsAsync();
        var router = new ExpertRouter(registry);
        var result = await router.SelectExpertsAsync("anything");
        var selection = Assert.Single(result.Selections);
        Assert.Equal("only", selection.ExpertId);
    }
}

internal sealed class TestExpert : IExpertModule
{
    public string ExpertId { get; }
    public ExpertDomain Domain { get; }
    public string CapabilityDescription { get; }
    public IReadOnlyList<string> KnowledgeTags { get; }
    public float MinConfidence => 0.3f;

    public TestExpert(string id, string description, ExpertDomain domain)
    {
        ExpertId = id;
        CapabilityDescription = description;
        Domain = domain;
        KnowledgeTags = [id];
    }

    public Task<ExpertResponse> QueryAsync(ExpertQuery query, CancellationToken ct = default)
    {
        return Task.FromResult(new ExpertResponse(
            ExpertId, $"Response from {ExpertId}",
            0.8f, [], new ProvenanceInfo("test", DateTime.UtcNow)));
    }
}
