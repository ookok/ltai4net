// Copyright (c) LTAI. All rights reserved.

using Microsoft.Extensions.Logging;

namespace LTAI.AI;

/// <summary>
/// Decorator over <see cref="EmbeddingClient"/> with explicit fallback
/// tier tracking and observability for routing diagnostics.
///
/// Tier order (0=best, 5=worst):
///   0 Local ONNX
///   1 Remote cache
///   2 Remote API
///   3 Local ONNX activated after repeated API failures
///   4 BM25 heuristic (FastEmb)
///   5 Empty/default
/// </summary>
public sealed class RetryChainEmbedder
{
    private readonly EmbeddingClient _inner;
    private readonly ILogger<RetryChainEmbedder>? _logger;

    public RetryChainEmbedder(
        EmbeddingClient inner,
        ILogger<RetryChainEmbedder>? logger = null)
    {
        _inner = inner;
        _logger = logger;
    }

    public EmbeddingTier LastTier { get; private set; } = EmbeddingTier.Onnx;
    private int _fallthroughs;
    public int TotalFallthroughs => _fallthroughs;

    public async Task<float[]> GenerateAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            LastTier = EmbeddingTier.Default;
            return [];
        }

        float[] result;
        try
        {
            result = await _inner.GenerateAsync(text, ct).ConfigureAwait(false);
        }
        catch
        {
            result = [];
        }

        if (result.Length > 0)
        {
            LastTier = InferTier();
            return result;
        }

        // Final fallback: BM25
        try
        {
            result = EmbeddingClient.FastEmb(text, _inner.Dimension);
            if (result.Length > 0)
            {
                LastTier = EmbeddingTier.Bm25;
                return result;
            }
        }
        catch { }

        Interlocked.Increment(ref _fallthroughs);
        LastTier = EmbeddingTier.Default;
        return [];
    }

    private EmbeddingTier InferTier()
    {
        if (_inner.Local?.Available == true)
            return EmbeddingTier.Onnx;
        if (_inner.LocalFallbackActivated)
            return EmbeddingTier.LocalFallback;
        if (_inner.ConsecutiveAllProviderFailures > 1)
            return EmbeddingTier.Bm25;
        return _inner.Local == null
            ? EmbeddingTier.RemoteApi
            : EmbeddingTier.Onnx;
    }

}

public enum EmbeddingTier
{
    Onnx = 0,
    RemoteCache = 1,
    RemoteApi = 2,
    LocalFallback = 3,
    Bm25 = 4,
    Default = 5,
}
