// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  NullCachingStore — Tier 3: graceful degradation fallback
//
//  No-op implementation of IMemoryCachingStore. Used when the
//  upper tiers are unavailable (disk full, DB corruption, etc.).
//
//  All methods return empty/false immediately — no allocations.
// ═══════════════════════════════════════════════════════════════

namespace LTAI.Agent.Caching;

/// <summary>
/// No-op fallback. All operations are immediate and return empty results.
/// Used as the lowest tier in the caching cascade.
/// </summary>
public sealed class NullCachingStore : IMemoryCachingStore
{
    public string ActiveTier => "Null";
    public int CheckpointCount => 0;

    public Task StoreAsync(string key, byte[] data, long tokenCount, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<byte[]?> LookupAsync(string key, CancellationToken ct = default)
        => Task.FromResult<byte[]?>(null);

    public Task<(string key, byte[] data, long tokenCount)?> FindNearestAsync(
        string sessionId, long tokenCount, CancellationToken ct = default)
        => Task.FromResult<(string, byte[], long)?>(null);

    public Task<IReadOnlyList<CheckpointSummary>> FindRangeAsync(
        string sessionId, long fromToken, long toToken, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CheckpointSummary>>([]);

    public Task InvalidateSessionAsync(string sessionId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task ClearAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    public void Dispose() { }
}
