// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  SemanticKvCache — semantic similarity-based KV cache
//
//  Phase 5b: user query embedding → vector search over similar
//  historical queries. When similarity > 0.92, reuse previous
//  KV cache (skip prefill for similar questions).
//
//  Phase 5c: LSH acceleration — 16-bit random projection index
//  prunes the O(n) full scan to O(candidates) where candidates
//  ≈ 2 on average for ≤1000 entries.
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
/// Thread-safe after construction. Uses LSH index (16-bit random
/// projection) to accelerate search.
/// </summary>
public sealed class SemanticKvCache : IDisposable
{
    private readonly EmbeddingClient _embedder;
    private readonly double _similarityThreshold;
    private readonly ConcurrentDictionary<string, CacheRecord> _store = new(StringComparer.Ordinal);
    private readonly ReaderWriterLockSlim _rwLock = new();
    private readonly int _maxEntries;
    private readonly TimeSpan _defaultTtl;
    private volatile bool _disposed;

    // ── LSH index ──
    private const int LshBits = 16;
    private readonly float[][] _projectionMatrix;
    private readonly ConcurrentDictionary<int, List<string>> _lshBuckets = new();

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
        public DateTime ExpiresAt { get; }
        public int HitCount;
        public int Signature;

        public CacheRecord(float[] embedding, byte[] kvData, DateTime cachedAt, DateTime expiresAt, int signature)
        {
            Embedding = embedding;
            KvData = kvData;
            CachedAt = cachedAt;
            ExpiresAt = expiresAt;
            HitCount = 0;
            Signature = signature;
        }

        public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
    }

    public SemanticKvCache(
        EmbeddingClient embedder,
        double similarityThreshold = 0.92,
        int maxEntries = 1000,
        TimeSpan? defaultTtl = null)
    {
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _similarityThreshold = similarityThreshold;
        _maxEntries = Math.Max(100, maxEntries);
        _defaultTtl = defaultTtl ?? TimeSpan.FromMinutes(30);

        _projectionMatrix = BuildProjectionMatrix(LshBits, embedder.Dimension);
    }

    // ═══════════════════════════════════════════
    //  LSH index
    // ═══════════════════════════════════════════

    private static float[][] BuildProjectionMatrix(int k, int dim)
    {
        var rng = Random.Shared;
        var matrix = new float[k][];
        for (int i = 0; i < k; i++)
        {
            var row = new float[dim];
            double norm = 0;
            for (int j = 0; j < dim; j++)
            {
                double u1 = 1.0 - rng.NextDouble();
                double u2 = 1.0 - rng.NextDouble();
                var val = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
                row[j] = val;
                norm += val * val;
            }
            norm = Math.Sqrt(norm);
            if (norm > 0)
                for (int j = 0; j < dim; j++)
                    row[j] = (float)(row[j] / norm);
            matrix[i] = row;
        }
        return matrix;
    }

    private int ComputeSignature(ReadOnlySpan<float> embedding)
    {
        int sig = 0;
        for (int i = 0; i < LshBits; i++)
        {
            var proj = _projectionMatrix[i].AsSpan();
            float dot = 0;
            int len = Math.Min(proj.Length, embedding.Length);
            for (int j = 0; j < len; j++)
                dot += proj[j] * embedding[j];
            if (dot > 0)
                sig |= 1 << i;
        }
        return sig;
    }

    private void AddToBucket(int sig, string query)
    {
        var list = _lshBuckets.GetOrAdd(sig, static _ => new List<string>());
        lock (list) { list.Add(query); }
    }

    private void RemoveFromBucket(int sig, string query)
    {
        if (_lshBuckets.TryGetValue(sig, out var list))
        {
            lock (list) { list.Remove(query); }
        }
    }

    private void CollectCandidates(int sig, HashSet<string> candidates)
    {
        AddBucketItems(sig, candidates);
        for (int i = 0; i < LshBits; i++)
            AddBucketItems(sig ^ (1 << i), candidates);
        for (int i = 0; i < LshBits; i++)
        {
            int maskI = 1 << i;
            for (int j = i + 1; j < LshBits; j++)
                AddBucketItems(sig ^ maskI ^ (1 << j), candidates);
        }
    }

    private void AddBucketItems(int sig, HashSet<string> candidates)
    {
        if (_lshBuckets.TryGetValue(sig, out var list))
        {
            lock (list)
            {
                foreach (var key in list)
                    candidates.Add(key);
            }
        }
    }

    // ═══════════════════════════════════════════
    //  Core operations
    // ═══════════════════════════════════════════

    /// <summary>
    /// Look up a query in the semantic cache.
    /// </summary>
    /// <param name="query">User query text.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Cached KV data if similar query found, null otherwise.</returns>
    public async Task<byte[]?> LookupAsync(string query, CancellationToken ct = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(query)) return null;

        // Generate embedding BEFORE entering lock to avoid serializing concurrent lookups
        var queryEmb = await _embedder.GenerateAsync(query, ct).ConfigureAwait(false);
        if (queryEmb.Length == 0) return null;

        var sig = ComputeSignature(queryEmb.AsSpan());
        var candidates = new HashSet<string>();

        _rwLock.EnterReadLock();
        try
        {
            if (_store.IsEmpty) return null;

            CollectCandidates(sig, candidates);
            if (candidates.Count == 0) return null;

            CacheRecord? bestMatch = null;
            double bestSimilarity = 0;
            var now = DateTime.UtcNow;

            foreach (var key in candidates)
            {
                if (!_store.TryGetValue(key, out var record)) continue;
                if (now >= record.ExpiresAt)
                {
                    _store.TryRemove(key, out _);
                    RemoveFromBucket(record.Signature, key);
                    continue;
                }
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
    /// <param name="ttl">Optional TTL override (defaults to <see cref="_defaultTtl"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task StoreAsync(string query, byte[] kvData, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        if (_disposed) return;

        var emb = await _embedder.GenerateAsync(query, ct).ConfigureAwait(false);
        if (emb.Length == 0) return;

        var sig = ComputeSignature(emb.AsSpan());
        var expiresAt = DateTime.UtcNow + (ttl ?? _defaultTtl);

        _rwLock.EnterWriteLock();
        try
        {
            var now = DateTime.UtcNow;
            foreach (var (k, v) in _store)
                if (now >= v.ExpiresAt)
                {
                    _store.TryRemove(k, out _);
                    RemoveFromBucket(v.Signature, k);
                }

            if (_store.Count >= _maxEntries)
            {
                var lru = _store.OrderBy(kv => kv.Value.HitCount).ThenBy(kv => kv.Value.CachedAt).FirstOrDefault();
                if (lru.Key != null)
                {
                    _store.TryRemove(lru.Key, out _);
                    RemoveFromBucket(lru.Value.Signature, lru.Key);
                }
            }

            _store[query] = new CacheRecord(emb, kvData, DateTime.UtcNow, expiresAt, sig);
            AddToBucket(sig, query);
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    /// <summary>Invalidate a specific query's cache entry.</summary>
    public void Invalidate(string query)
    {
        if (_store.TryRemove(query, out var record))
            RemoveFromBucket(record.Signature, query);
    }

    /// <summary>Clear all cached entries.</summary>
    public void Clear()
    {
        _rwLock.EnterWriteLock();
        try
        {
            _store.Clear();
            _lshBuckets.Clear();
        }
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
                records.Length, totalBytes, totalHits, _similarityThreshold, _defaultTtl);
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    private static float CosineSimilarity(float[] a, float[] b)
        => VectorMath.CosineSimilarity(a.AsSpan(), b.AsSpan());

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _rwLock.Dispose();
        _store.Clear();
        _lshBuckets.Clear();
    }
}

/// <summary>Diagnostic stats for SemanticKvCache.</summary>
public sealed record SemanticCacheStats(
    int EntryCount,
    long TotalBytes,
    int TotalHits,
    double SimilarityThreshold,
    TimeSpan DefaultTtl);
