using LTAI.AI;
using Xunit;

namespace LTAI.Tests;

/// <summary>
/// 集成测试：验证 LocalEmbedder (ONNX) 向量推理管线正常工作。
/// 需要 models/minilm-l6-v2/model.onnx 文件存在。
/// 可用 dotnet test --filter "EmbeddingIntegration" 单独运行。
/// </summary>
[Trait("Category", "Integration")]
public class EmbeddingIntegrationTests
{
    /// <summary>
    /// ONNX 模型加载 + 向量推理核心路径：
    /// 输入一段文本 → 输出 384 维归一化向量。
    /// </summary>
    [Fact]
    public void LocalEmbedder_Generates384DimNormalizedVector()
    {
        using var embedder = new LocalEmbedder();

        // Skip if model file not present (dev/CI without the 90MB model)
        if (!embedder.Available)
            return;

        var vec = embedder.Generate("C# async await Task Parallel Library patterns");

        Assert.NotNull(vec);
        Assert.Equal(384, vec.Length);

        // Verify L2 normalized (unit vector)
        var norm = MathF.Sqrt(vec.Sum(f => f * f));
        Assert.InRange(norm, 0.99f, 1.01f);
    }

    /// <summary>
    /// 空/空白文本输入 — 不应崩溃，返回 384 维向量。
    /// </summary>
    [Fact]
    public void LocalEmbedder_EmptyString_ReturnsReasonableVector()
    {
        using var embedder = new LocalEmbedder();
        if (!embedder.Available)
            return;

        var vec = embedder.Generate("");
        Assert.NotNull(vec);
        Assert.Equal(384, vec.Length);
    }

    /// <summary>
    /// 相似文本的向量余弦相似度 > 不相似文本。
    /// </summary>
    [Fact]
    public void LocalEmbedder_SimilarTexts_HigherCosineThanUnrelated()
    {
        using var embedder = new LocalEmbedder();
        if (!embedder.Available)
            return;

        var v1 = embedder.Generate("machine learning neural network deep learning");
        var v2 = embedder.Generate("deep learning model training inference");
        var v3 = embedder.Generate("banana apple fruit nutrition vitamins");

        var simRelated = CosineSimilarity(v1, v2);
        var simUnrelated = CosineSimilarity(v1, v3);

        Assert.True(simRelated > simUnrelated,
            $"Related ({simRelated:F4}) should be > Unrelated ({simUnrelated:F4})");
    }

    /// <summary>
    /// EmbeddingClient 的 ONNX 主路径：GenerateAsync 应优先返回本地 ONNX 向量。
    /// （不依赖任何 API key）
    /// </summary>
    [Fact]
    public async Task EmbeddingClient_OnnxPrimaryPath_WhenLocalAvailable()
    {
        using var local = new LocalEmbedder();
        if (!local.Available)
            return;

        var client = new EmbeddingClient(
            new HttpClientFactoryStub(),
            local);

        var vec = await client.GenerateAsync("test embedding with ONNX");

        Assert.NotNull(vec);
        Assert.Equal(384, vec.Length);

        var norm = MathF.Sqrt(vec.Sum(f => f * f));
        Assert.InRange(norm, 0.99f, 1.01f);
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        int len = Math.Min(a.Length, b.Length);
        float dot = 0, na = 0, nb = 0;
        for (int i = 0; i < len; i++)
        { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na > 0 && nb > 0 ? dot / (MathF.Sqrt(na) * MathF.Sqrt(nb)) : 0;
    }
}

/// <summary>Stub IHttpClientFactory for tests that don't make real HTTP calls.</summary>
file sealed class HttpClientFactoryStub : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        new HttpClient(new NoopHandler());
}

file sealed class NoopHandler : HttpClientHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{}")
        });
}
