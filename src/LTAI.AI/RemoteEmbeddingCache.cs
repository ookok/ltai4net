// Copyright (c) LTAI. All rights reserved.

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.AI;

/// <summary>
/// P14.5: in-memory TTL cache for remote embedding API results. Distinct from
/// <see cref="ToolEmbeddingCache"/> (which persists tool/agent description
/// embeddings to JSON). This cache holds transient per-request embeddings with
/// a TTL (default 24h) to bound stale risk when remote providers upgrade their
/// models. Keyed by <c>provider+model+SHA256(text)</c> so provider swaps
/// don't pollute results.
/// </summary>
/// <remarks>
/// <para><b>Use cases:</b></para>
/// <list type="bullet">
/// <item><description>DecisionTreeRouter task text — repeats across same-domain sessions.</description></item>
/// <item><description>Reranker candidate texts — repeat across queries (high hit rate).</description></item>
/// <item><description>AgentRegistry top-K candidate descriptions — long sessions see repeats.</description></item>
/// </list>
/// <para><b>NOT cached:</b> Local ONNX path (fast, deterministic, free).</para>
/// <para><b>TTL rationale (D54):</b> 24h bounds the window where a remote
/// provider model upgrade could leave stale vectors. After 24h we recompute.</para>
/// <para><b>Disk persistence:</b> NONE — process-local only. Restart loses
/// cache (cold start hits API once, then warms).</para>
/// </remarks>
public sealed class RemoteEmbeddingCache
{
    private readonly ILogger<RemoteEmbeddingCache> _logger;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, CacheEntry> _store = new(StringComparer.Ordinal);
    private long _hits;
    private long _misses;
    private long _evictions;

    public RemoteEmbeddingCache(
        TimeSpan? ttl = null,
        ILogger<RemoteEmbeddingCache>? logger = null)
    {
        _ttl = ttl ?? TimeSpan.FromHours(24);
        _logger = logger ?? NullLogger<RemoteEmbeddingCache>.Instance;
    }

    public TimeSpan Ttl => _ttl;
    public int CachedEntryCount => _store.Count;
    public long CacheHits => Interlocked.Read(ref _hits);
    public long CacheMisses => Interlocked.Read(ref _misses);
    public long CacheLookups => CacheHits + CacheMisses;
    public long Evictions => Interlocked.Read(ref _evictions);
    public double HitRate => CacheLookups == 0 ? 0d : (double)CacheHits / CacheLookups;

    /// <summary>
    /// Attempt to fetch a cached vector for <paramref name="text"/> from
    /// <paramref name="provider"/> using <paramref name="model"/>. Returns
    /// <c>true</c> on hit, <c>false</c> on miss or expired entry.
    /// </summary>
    public bool TryGet(string provider, string model, string text, out float[]? vector)
    {
        var key = BuildKey(provider, model, text);
        if (_store.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresUtc > DateTime.UtcNow)
            {
                Interlocked.Increment(ref _hits);
                vector = entry.Vector;
                return true;
            }
            // Expired — lazy evict
            if (_store.TryRemove(key, out _))
            {
                Interlocked.Increment(ref _evictions);
                _logger.LogDebug("RemoteEmbeddingCache: expired entry evicted ({Provider}/{Model})", provider, model);
            }
        }
        Interlocked.Increment(ref _misses);
        vector = null;
        return false;
    }

    /// <summary>
    /// Store a freshly-computed vector in the cache. Overwrites any existing
    /// entry. Expires after the configured TTL.
    /// </summary>
    public void Store(string provider, string model, string text, float[] vector)
    {
        var key = BuildKey(provider, model, text);
        _store[key] = new CacheEntry
        {
            Vector = vector,
            ExpiresUtc = DateTime.UtcNow + _ttl,
        };
    }

    /// <summary>
    /// Sweep all expired entries. Returns the number of entries evicted.
    /// Cheap to call occasionally (e.g., every Nth miss). Not required for
    /// correctness — <see cref="TryGet"/> lazily evicts on access.
    /// </summary>
    public int SweepExpired()
    {
        var now = DateTime.UtcNow;
        var keys = _store.Where(kv => kv.Value.ExpiresUtc <= now).Select(kv => kv.Key).ToArray();
        var evicted = 0;
        foreach (var k in keys)
        {
            if (_store.TryRemove(k, out _))
            {
                Interlocked.Increment(ref _evictions);
                evicted++;
            }
        }
        if (evicted > 0)
            _logger.LogDebug("RemoteEmbeddingCache: swept {N} expired entries", evicted);
        return evicted;
    }

    /// <summary>Clear all entries. Returns the number of entries removed.</summary>
    public int Clear()
    {
        var n = _store.Count;
        _store.Clear();
        return n;
    }

    private static string BuildKey(string provider, string model, string text)
    {
        // Use first 32 hex chars (128-bit) of SHA-256 — collision risk
        // negligible for typical LLM query lengths (< 1KB).
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        var hex = Convert.ToHexString(hash, 0, 16);
        return $"{provider}:{model}:{hex}";
    }

    private sealed class CacheEntry
    {
        public float[] Vector { get; init; } = [];
        public DateTime ExpiresUtc { get; init; }
    }
}
