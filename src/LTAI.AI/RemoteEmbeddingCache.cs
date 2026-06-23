using LTAI.Core.Caching;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.AI;

/// <summary>
/// TTL cache for remote embedding API results, backed by unified <see cref="LTAICache{TKey,TValue}"/>.
/// Keyed by <c>provider+model+hash(text)</c> so provider swaps don't pollute results.
/// </summary>
public sealed class RemoteEmbeddingCache
{
    private readonly LTAICache<string, float[]> _cache;
    private readonly ILogger<RemoteEmbeddingCache> _logger;

    public RemoteEmbeddingCache(
        LTAICache<string, float[]> cache,
        ILogger<RemoteEmbeddingCache>? logger = null)
    {
        _cache = cache;
        _logger = logger ?? NullLogger<RemoteEmbeddingCache>.Instance;
    }

    public RemoteEmbeddingCache(
        TimeSpan? ttl = null,
        ILogger<RemoteEmbeddingCache>? logger = null,
        int maxEntries = 10000)
        : this(new LTAICache<string, float[]>(new LTAICacheOptions
        {
            MaxEntries = maxEntries,
            DefaultTtl = ttl ?? TimeSpan.FromHours(24)
        }), logger)
    {
    }

    public int CachedEntryCount => _cache.Count;
    public long CacheHits => _cache.Hits;
    public long CacheMisses => _cache.Misses;
    public long CacheLookups => CacheHits + CacheMisses;
    public long Evictions => _cache.Evictions;
    public double HitRate => _cache.HitRate;
    public LTAICacheMetrics Metrics => _cache.Metrics;

    public bool TryGet(string provider, string model, string text, out float[]? vector)
    {
        var key = BuildKey(provider, model, text);
        if (_cache.TryGet(key, out vector))
        {
            _logger.LogDebug("RemoteEmbeddingCache HIT: {Provider}/{Model}", provider, model);
            return true;
        }
        return false;
    }

    public void Store(string provider, string model, string text, float[] vector)
    {
        var key = BuildKey(provider, model, text);
        _cache.Set(key, vector);
    }

    public int SweepExpired() => _cache.SweepExpired();

    public int Clear()
    {
        var n = _cache.Count;
        _cache.Clear();
        return n;
    }

    private static string BuildKey(string provider, string model, string text)
        => $"{provider}:{model}:{FastHash.ComputeHex(text)}";
}
