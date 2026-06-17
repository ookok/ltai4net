using LTAI.AI;
using Xunit;

namespace LTAI.Tests;

public class Glove50EmbedderTests
{
    private readonly Glove50Embedder _embedder = new();

    [Fact]
    public void Embed_Returns50DVector()
    {
        var vec = _embedder.Embed("hello world");
        Assert.Equal(50, vec.Length);
    }

    [Fact]
    public void Embed_ReturnsUnitVector()
    {
        var vec = _embedder.Embed("code review");
        var norm = Math.Sqrt(vec.Sum(v => v * v));
        Assert.Equal(1.0, norm, 5);
    }

    [Fact]
    public void Embed_SimilarTexts_HigherThanIdentity()
    {
        // Same text should have cosine similarity ≈ 1.0
        var v1 = _embedder.Embed("code review");
        var v2 = _embedder.Embed("code review");
        var diff = _embedder.Embed("apple orange banana completely different");

        var simSame = CosineSimilarity(v1, v2);
        var simDiff = CosineSimilarity(v1, diff);

        Assert.True(simSame > 0.99, $"same text similarity should be ~1.0, got {simSame:F4}");
        Assert.True(simSame > simDiff,
            $"same={simSame:F4} should be > different={simDiff:F4}");
    }

    [Fact]
    public void Embed_Deterministic()
    {
        var v1 = _embedder.Embed("database query optimization");
        var v2 = _embedder.Embed("database query optimization");

        Assert.Equal(v1.Length, v2.Length);
        for (int i = 0; i < v1.Length; i++)
            Assert.Equal(v1[i], v2[i], precision: 5);
    }

    [Fact]
    public void Embed_EmptyText_ReturnsZeroVector()
    {
        var vec = _embedder.Embed("");
        Assert.Equal(50, vec.Length);
        Assert.All(vec, v => Assert.Equal(0, v));
    }

    [Fact]
    public void EmbedBatch_ReturnsCorrectCount()
    {
        var results = _embedder.EmbedBatch(["hello", "world", "test"]);
        Assert.Equal(3, results.Count);
        Assert.All(results, v => Assert.Equal(50, v.Length));
    }

    [Fact]
    public void VocabularySize_ReturnsPositive()
    {
        Assert.True(_embedder.VocabularySize > 0);
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return dot / (float)(Math.Sqrt(na) * Math.Sqrt(nb) + 1e-10);
    }
}
