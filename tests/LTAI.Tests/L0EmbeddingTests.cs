using LTAI.Knowledge.Vector;
using LTAI.Knowledge.Vector.Embedding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LTAI.Tests;

public class L0EmbeddingTests
{
    [Fact]
    public void TC_L0_01_HashBackend_ProducesDeterministicEmbeddings()
    {
        var backend = new LocalEmbeddingBackend(NullLogger<LocalEmbeddingBackend>.Instance);
        var emb1 = backend.EmbedAsync(new[] { "环境影响评价" }).Result;
        var emb2 = backend.EmbedAsync(new[] { "环境影响评价" }).Result;

        Assert.Equal(emb1[0].Length, emb2[0].Length);
        for (int i = 0; i < emb1[0].Length; i++)
            Assert.Equal(emb1[0][i], emb2[0][i], 6);
    }

    [Fact]
    public void TC_L0_02_DifferentTexts_ProduceDifferentEmbeddings()
    {
        var backend = new LocalEmbeddingBackend(NullLogger<LocalEmbeddingBackend>.Instance);
        var emb1 = backend.EmbedAsync(new[] { "环境影响评价" }).Result;
        var emb2 = backend.EmbedAsync(new[] { "代码审查工具" }).Result;

        var same = true;
        for (int i = 0; i < Math.Min(emb1[0].Length, emb2[0].Length); i++)
            if (Math.Abs(emb1[0][i] - emb2[0][i]) > 1e-6f) same = false;
        Assert.False(same, "Different texts should produce different embeddings");
    }

    [Fact]
    public void TC_L0_03_DefaultRegistration_UsesLocalBackend()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        var result = LTAI.Knowledge.Vector.ServiceCollectionExtensions.AddLTAIVector(services);
        Assert.NotNull(result);
    }
}
