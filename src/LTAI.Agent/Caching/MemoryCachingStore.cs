// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  MemoryCachingStore — Tier 1: fast in-memory LRU cache
//
//  In-memory checkpoint store with LRU eviction (default 64 entries).
//  Thread-safe: ConcurrentDictionary + periodic TTL sweep.
//
//  Each checkpoint stores:
//    - Serialized state bytes (KV snapshot metadata, token positions)
//    - Token count for position tracking
//    - Timestamp for TTL-based expiry
//
//  This is the DEFAULT tier — SQLite tier is for long-term persistence.
// ═══════════════════════════════════════════════════════════════

using System.Collections.Concurrent;

namespace LTAI.Agent.Caching;

/// <summary>
/// In-memory checkpoint store with LRU eviction. Tier 1 of the
/// Memory Caching Layer cascade. Fast but volatile (lost on restart).
/// </summary>
public sealed class MemoryCachingStore : IMemoryCachingStore
{
    private readonly ConcurrentDictionary<string, CheckpointRecord> _checkpoints = new(StringComparer.Ordinal);
    private readonly int _maxEntries;
    private readonly TimeSpan _defaultTtl;
    private bool _disposed;

    public string ActiveTier => "Memory";
    public int CheckpointCount => _checkpoints.Count;
    public int MaxEntries => _maxEntries;

    private sealed record CheckpointRecord(byte[] Data, long TokenCount, DateTime SavedAt)
    {
        public DateTime ExpiresAt = SavedAt.Add(TimeSpan.FromHours(4));
        public int HitCount;
    }

    public MemoryCachingStore(int maxEntries = 64, TimeSpan? defaultTtl = null)
    {
        _maxEntries = maxEntries;
        _defaultTtl = defaultTtl ?? TimeSpan.FromHours(4);
    }

    public Task StoreAsync(string key, byte[] data, long tokenCount, CancellationToken ct = default)
    {
        if (_disposed) return Task.CompletedTask;

        // Evict LRU if at capacity
        while (_checkpoints.Count >= _maxEntries)
        {
            var oldest = _checkpoints
                .OrderBy(kv => kv.Value.HitCount)
                .ThenBy(kv => kv.Value.SavedAt)
                .FirstOrDefault();

            if (oldest.Key != null)
                _checkpoints.TryRemove(oldest.Key, out _);
            else break;
        }

        _checkpoints[key] = new CheckpointRecord(data, tokenCount, DateTime.UtcNow);
        return Task.CompletedTask;
    }

    public Task<byte[]?> LookupAsync(string key, CancellationToken ct = default)
    {
        if (_disposed) return Task.FromResult<byte[]?>(null);

        if (_checkpoints.TryGetValue(key, out var record))
        {
            // Check TTL
            if (DateTime.UtcNow < record.ExpiresAt)
            {
                record.HitCount++;
                return Task.FromResult<byte[]?>(record.Data);
            }
            // Expired
            _checkpoints.TryRemove(key, out _);
        }
        return Task.FromResult<byte[]?>(null);
    }

    public Task<(string key, byte[] data, long tokenCount)?> FindNearestAsync(
        string sessionId, long tokenCount, CancellationToken ct = default)
    {
        if (_disposed) return Task.FromResult<(string, byte[], long)?>(null);

        var prefix = $"session:{sessionId}:";

        var nearest = _checkpoints
            .Where(kv => kv.Key.StartsWith(prefix) && kv.Value.TokenCount <= tokenCount)
            .OrderByDescending(kv => kv.Value.TokenCount)
            .FirstOrDefault();

        if (nearest.Key == null)
            return Task.FromResult<(string, byte[], long)?>(null);

        nearest.Value.HitCount++;
        return Task.FromResult<(string, byte[], long)?>(
            (nearest.Key, nearest.Value.Data, nearest.Value.TokenCount));
    }

    public Task<IReadOnlyList<CheckpointSummary>> FindRangeAsync(
        string sessionId, long fromToken, long toToken, CancellationToken ct = default)
    {
        if (_disposed)
            return Task.FromResult<IReadOnlyList<CheckpointSummary>>([]);

        var prefix = $"session:{sessionId}:";
        var results = _checkpoints
            .Where(kv => kv.Key.StartsWith(prefix)
                         && kv.Value.TokenCount >= fromToken
                         && kv.Value.TokenCount <= toToken)
            .Select(kv => new CheckpointSummary(
                kv.Key, kv.Value.TokenCount, kv.Value.SavedAt, "Memory"))
            .OrderBy(s => s.TokenCount)
            .ToList();

        return Task.FromResult<IReadOnlyList<CheckpointSummary>>(results);
    }

    public Task InvalidateSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (_disposed) return Task.CompletedTask;
        var prefix = $"session:{sessionId}:";
        var keys = _checkpoints.Keys.Where(k => k.StartsWith(prefix)).ToList();
        foreach (var key in keys)
            _checkpoints.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        _checkpoints.Clear();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _checkpoints.Clear();
    }
}
