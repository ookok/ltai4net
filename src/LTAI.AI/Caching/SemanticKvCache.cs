// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  SemanticKvCache — semantic similarity-based KV cache
//
//  Phase 5b: user query embedding → vector search over similar
//  historical queries. When similarity > 0.92, reuse previous
//  KV cache (skip prefill for similar questions).
//
//  Requires EmbeddingClient for query->vector conversion and
//  a provider that supports start_from (e.g. DeepSeek).
// ═══════════════════════════════════════════════════════════════

using System.Collections.Concurrent;

namespace LTAI.AI.Caching;

/// <summary>
/// Semantic KV cache: embeds user queries and searches for similar
/// previously-cached queries. When cosine similarity exceeds the
/// threshold (default 0.92), reuses the cached KV state.
///
/// Thread-safe after construction. Uses InMemoryVectorStore for
/// the vector index (small scale: ≤1000 entries).
/// </summary>
public sealed class SemanticKvCache : IDisposable
{
    private readonly EmbeddingClient _embedder;
    private readonly double _similarityThreshold;
    private readonly ConcurrentDictionary<string, CacheRecord> _store = new(StringComparer.Ordinal);
    private readonly ReaderWriterLockSlim _rwLock = new();
    private readonly int _maxEntries;
    private volatile bool _disposed;

    /// <summary>Number of cached entries.</summary>
    public int Count => _store.Count;

    /// <summary>Similarity threshold for cache hit.</summary>
    public double SimilarityThreshold => _similarityThreshold;

    /// <summary>A single cached query with its KV data.</summary>
    private sealed class CacheRecord
    {
        public float[] Embedding { get; }
        public byte[] KvData { get; }
        public DateTime CachedAt { get; }
        public int HitCount;

        public CacheRecord(float[] embedding, byte[] kvData, DateTime cachedAt)
        {
            Embedding = embedding;
            KvData = kvData;
            CachedAt = cachedAt;
            HitCount = 0;
        }
    }

    public SemanticKvCache(
        EmbeddingClient embedder,
        double similarityThreshold = 0.92,
        int maxEntries = 1000)
    {
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _similarityThreshold = similarityThreshold;
        _maxEntries = Math.Max(100, maxEntries);
    }

    /// <summary>
    /// Look up a query in the semantic cache.
    /// </summary>
    /// <param name="query">User query text.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Cached KV data if similar query found, null otherwise.</returns>
    public async Task<byte[]?> LookupAsync(string query, CancellationToken ct = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(query)) return null;

        _rwLock.EnterReadLock();
        try
        {
            if (_store.IsEmpty) return null;

            // Embed the query
            var queryEmb = await _embedder.GenerateAsync(query, ct).ConfigureAwait(false);
            if (queryEmb.Length == 0) return null;

            // Search for similar cached queries
            CacheRecord? bestMatch = null;
            double bestSimilarity = 0;

            foreach (var (_, record) in _store)
            {
                var sim = CosineSimilarity(queryEmb, record.Embedding);
                if (sim > bestSimilarity)
                {
                    bestSimilarity = sim;
                    bestMatch = record;
                }
            }

            if (bestMatch != null && bestSimilarity >= _similarityThreshold)
            {
                Interlocked.Increment(ref bestMatch.HitCount);
                return bestMatch.KvData;
            }

            return null;
        }
        finally { _rwLock.ExitReadLock(); }
    }

    /// <summary>
    /// Store a query and its KV cache data.
    /// </summary>
    /// <param name="query">Original user query.</param>
    /// <param name="kvData">Serialized KV cache data.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task StoreAsync(string query, byte[] kvData, CancellationToken ct = default)
    {
        if (_disposed) return;

        var emb = await _embedder.GenerateAsync(query, ct).ConfigureAwait(false);
        if (emb.Length == 0) return;

        _rwLock.EnterWriteLock();
        try
        {
            // LRU eviction: evict least-hit entry when at capacity
            if (_store.Count >= _maxEntries)
            {
                var lru = _store.OrderBy(kv => kv.Value.HitCount).ThenBy(kv => kv.Value.CachedAt).FirstOrDefault();
                if (lru.Key != null) _store.TryRemove(lru.Key, out _);
            }
            _store[query] = new CacheRecord(emb, kvData, DateTime.UtcNow);
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    /// <summary>Invalidate a specific query's cache entry.</summary>
    public void Invalidate(string query)
    {
        _store.TryRemove(query, out _);
    }

    /// <summary>Clear all cached entries.</summary>
    public void Clear()
    {
        _rwLock.EnterWriteLock();
        try { _store.Clear(); }
        finally { _rwLock.ExitWriteLock(); }
    }

    /// <summary>Get diagnostic stats.</summary>
    public SemanticCacheStats GetStats()
    {
        _rwLock.EnterReadLock();
        try
        {
            var records = _store.Values.ToArray();
            long totalBytes = records.Sum(r => r.KvData.Length);
            int totalHits = records.Sum(r => r.HitCount);
            return new SemanticCacheStats(
                records.Length, totalBytes, totalHits, _similarityThreshold);
        }
        finally { _rwLock.ExitReadLock(); }
    }

    private static float CosineSimilarity(float[] a, float[] b)
        => VectorMath.CosineSimilarity(a.AsSpan(), b.AsSpan());

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _rwLock.Dispose();
        _store.Clear();
    }
}

/// <summary>Diagnostic stats for SemanticKvCache.</summary>
public sealed record SemanticCacheStats(
    int EntryCount,
    long TotalBytes,
    int TotalHits,
    double SimilarityThreshold);
