using System.Security.Cryptography;
using System.Text;
using LTAI.Vector.Interfaces;
using Microsoft.Extensions.Logging;

namespace LTAI.Vector.Embedding;

public sealed class LocalEmbeddingBackend : IEmbeddingBackend, IDisposable
{
    private readonly ILogger<LocalEmbeddingBackend> _logger;
    private const int EmbeddingDim = 384;
    private const int FallbackDim = 128;

    public int Dimension => EmbeddingDim;

    public LocalEmbeddingBackend(ILogger<LocalEmbeddingBackend> logger)
    {
        _logger = logger;
        _logger.LogInformation("LocalEmbeddingBackend initialized with dimension {Dim}", EmbeddingDim);
    }

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        var results = new float[texts.Count][];
        for (var i = 0; i < texts.Count; i++)
            results[i] = EmbedSingle(texts[i], EmbeddingDim);

        return Task.FromResult(results);
    }

    private static float[] EmbedSingle(string text, int dim)
    {
        var vec = new float[dim];
        var chars = text.ToCharArray();
        var len = chars.Length;

        if (len == 0)
            return vec;

        var charCounts = new Dictionary<char, int>();
        foreach (var c in chars)
        {
            if (!charCounts.ContainsKey(c))
                charCounts[c] = 0;
            charCounts[c]++;
        }

        foreach (var (c, count) in charCounts)
        {
            var idx = c % dim;
            vec[idx] += (float)count / len;
        }

        for (var i = 0; i < len - 1; i++)
        {
            var bigram = (chars[i] << 8) | chars[i + 1];
            var idx = (int)((uint)bigram % (uint)dim);
            vec[idx] += 0.3f / len;
        }

        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(text));
        for (var i = 0; i < hash.Length; i++)
        {
            var idx = hash[i] % dim;
            vec[idx] += (hash[i] - 127.5f) * 0.001f;
        }

        var norm = 0f;
        for (var i = 0; i < dim; i++)
            norm += vec[i] * vec[i];

        norm = MathF.Sqrt(norm);
        if (norm > 1e-9f)
        {
            for (var i = 0; i < dim; i++)
                vec[i] /= norm;
        }

        return vec;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
