// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  PrefixKvCache — string prefix-based KV cache
//
//  Phase 5a: system prompt + history summary → SHA-256 prefix key.
//  When a subsequent request has the same system prompt prefix,
//  the KV cache is reused (skipping the prefill phase).
//
//  In-memory ConcurrentDictionary with TTL. Thread-safe.
// ═══════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using LTAI.Core.Configuration;
using System.Text;

namespace LTAI.AI.Caching;

/// <summary>
/// Prefix-based KV cache using SHA-256 hashing on
/// (system prompt + history summary). Thread-safe.
///
/// When a request has the same prefix key as a cached entry,
/// the prefill phase can be skipped (the KV cache is reused).
/// This works with providers that support "start_from" parameter
/// (DeepSeek supports this; OpenAI does not).
/// </summary>
public sealed class PrefixKvCache : IKvCacheStore
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly TimeSpan _defaultTtl;
    private volatile bool _disposed;

    /// <summary>Number of cache entries.</summary>
    public int Count => _cache.Count;

    /// <summary>Default TTL for cache entries.</summary>
    public TimeSpan DefaultTtl => _defaultTtl;

    /// <summary>
    /// Time-based cleanup interval. Runs when Store is called.
    /// </summary>
    public static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);

    private DateTime _lastCleanup = DateTime.UtcNow;

    public PrefixKvCache(TimeSpan? defaultTtl = null)
    {
        _defaultTtl = defaultTtl ?? TimeSpan.FromMinutes(5);
    }

    /// <inheritdoc />
    public byte[]? Lookup(string key)
    {
        if (_disposed) return null;
        CleanupIfNeeded();

        if (_cache.TryGetValue(key, out var entry))
        {
            if (DateTime.UtcNow < entry.ExpiresAt)
            {
                Interlocked.Increment(ref entry.HitCount);
                return entry.Data;
            }
            // Expired — remove
            _cache.TryRemove(key, out _);
        }
        return null;
    }

    /// <inheritdoc />
    public void Store(string key, byte[] data, TimeSpan? ttl = null)
    {
        if (_disposed) return;
        CleanupIfNeeded();

        _cache[key] = new CacheEntry(
            data,
            DateTime.UtcNow + (ttl ?? _defaultTtl));
    }

    /// <inheritdoc />
    public void Invalidate(string key)
    {
        _cache.TryRemove(key, out _);
    }

    /// <inheritdoc />
    public void Clear()
    {
        _cache.Clear();
    }

    /// <summary>
    /// Build a cache key from system prompt text + history summary.
    /// Uses SHA-256 for deterministic, collision-resistant keys.
    /// </summary>
    /// <param name="systemPrompt">System prompt text.</param>
    /// <param name="historySummary">Optional history summary.</param>
    /// <returns>Hex-encoded SHA-256 prefix key.</returns>
    public static string BuildKey(string systemPrompt, string? historySummary = null)
    {
        var sb = new StringBuilder();
        sb.Append("sys:").Append(systemPrompt.Trim());

        if (!string.IsNullOrEmpty(historySummary))
            sb.Append("|hist:").Append(historySummary.Trim());

        return FastHash.ComputeHex(sb.ToString());
    }

    /// <summary>
    /// Get diagnostic info about the cache (hits, entries, size).
    /// </summary>
    public CacheStats GetStats()
    {
        var entries = _cache.ToArray();
        long totalBytes = 0;
        int totalHits = 0;
        foreach (var (_, e) in entries)
        {
            totalBytes += e.Data.Length;
            totalHits += e.HitCount;
        }

        return new CacheStats(
            entries.Length,
            totalBytes,
            totalHits,
            _defaultTtl);
    }

    /// <summary>Get all current cache keys (for debugging).</summary>
    public IReadOnlyList<string> GetKeys() => _cache.Keys.ToArray();

    private void CleanupIfNeeded()
    {
        if (DateTime.UtcNow - _lastCleanup < CleanupInterval) return;
        _lastCleanup = DateTime.UtcNow;

        var now = DateTime.UtcNow;
        foreach (var (key, entry) in _cache)
        {
            if (now >= entry.ExpiresAt)
                _cache.TryRemove(key, out _);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cache.Clear();
    }

    private sealed record CacheEntry(byte[] Data, DateTime ExpiresAt)
    {
        public int HitCount;
    }
}

/// <summary>Diagnostic cache statistics.</summary>
public sealed record CacheStats(
    int EntryCount,
    long TotalBytes,
    int TotalHits,
    TimeSpan DefaultTtl);
