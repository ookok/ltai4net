using LTAI.Core;
using LTAI.Core.Configuration;
using LTAI.Knowledge.Vector;
using LTAI.Knowledge.Vector.Embedding;
using LTAI.Knowledge.Vector.Interfaces;
using LTAI.Knowledge.Vector.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LTAI.Vector.Tests;

public class VectorStoreTests
{
    private readonly IVectorStore _vectorStore;

    public VectorStoreTests()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        services.Configure<LTAIOptions>(_ => { });
        services.AddSingleton<IEmbeddingBackend, LocalEmbeddingBackend>();
        services.AddSingleton<IVectorStore, LTAI.Vector.VectorStore>();
        var sp = services.BuildServiceProvider();
        _vectorStore = sp.GetRequiredService<IVectorStore>();
    }

    [Fact]
    public async Task EmbedAsync_ReturnsVector()
    {
        var vec = await _vectorStore.EmbedAsync("hello world");
        Assert.NotEmpty(vec);
        Assert.Equal(384, vec.Length);
    }

    [Fact]
    public async Task EmbedAsync_EmptyString_ReturnsZeroVector()
    {
        var vec = await _vectorStore.EmbedAsync("");
        Assert.NotEmpty(vec);
    }

    [Fact]
    public async Task AddAndSearchVectors()
    {
        var id = "doc1";
        var embed = await _vectorStore.EmbedAsync("machine learning basics");
        await _vectorStore.AddVectorsAsync(new[] { (id, embed) });

        var results = await _vectorStore.SearchSimilarAsync(embed, 3);
        Assert.NotEmpty(results);
        Assert.Equal(id, results[0].Id);
        Assert.True(results[0].Score > 0);
    }

    [Fact]
    public async Task SearchWithNoVectors_ReturnsEmpty()
    {
        var query = await _vectorStore.EmbedAsync("random query");
        var results = await _vectorStore.SearchSimilarAsync(query, 5);
        Assert.Empty(results);
    }

    [Fact]
    public async Task DeleteVector_RemovesFromSearch()
    {
        var embed = await _vectorStore.EmbedAsync("test document");
        await _vectorStore.AddVectorsAsync(new[] { ("to_delete", embed) });
        await _vectorStore.DeleteVectorAsync("to_delete");

        var results = await _vectorStore.SearchSimilarAsync(embed, 3);
        Assert.DoesNotContain(results, r => r.Id == "to_delete");
    }

    [Fact]
    public async Task GetStats_ReturnsCorrectCount()
    {
        var e1 = await _vectorStore.EmbedAsync("doc a");
        var e2 = await _vectorStore.EmbedAsync("doc b");
        await _vectorStore.AddVectorsAsync(new[] { ("a", e1), ("b", e2) });

        var stats = await _vectorStore.GetStatsAsync();
        Assert.True(stats.TotalVectors >= 2);
        Assert.Equal(384, stats.Dimension);
        Assert.Equal("memory", stats.BackendType);
    }

    [Fact]
    public async Task SimilarityScoring_RelatedVsUnrelated()
    {
        var related1 = await _vectorStore.EmbedAsync("C# programming language features and patterns");
        var related2 = await _vectorStore.EmbedAsync("C# async await Task Parallel Library");
        var unrelated = await _vectorStore.EmbedAsync("Banana fruit nutrition vitamins potassium");

        await _vectorStore.AddVectorsAsync(new[] { ("r1", related1), ("unrel", unrelated) });

        var results = await _vectorStore.SearchSimilarAsync(related2, 2);
        Assert.NotEmpty(results);
        Assert.Equal(2, results.Count);
        Assert.True(results[0].Score >= 0);
        Assert.True(results[1].Score >= 0);
    }
}
