// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  CachingCascade — three-tier automatic fallback
//
//  Tier 1: MemoryCachingStore (fast, volatile, 64 LRU)
//  Tier 2: FileCachingStore (JSON file, crash-safe via atomic rename)
//  Tier 3: NullCachingStore (graceful degradation)
//
//  Write: all tiers (write-through)
//  Read: Tier 1 → Tier 2 → Tier 3 (first hit wins)
// ═══════════════════════════════════════════════════════════════

namespace LTAI.Agent.Caching;

/// <summary>
/// Three-tier caching cascade. Writes propagate to all tiers;
/// reads hit the first tier that has the data.
///
/// Thread-safe after construction (tiers are thread-safe).
/// </summary>
public sealed class CachingCascade : IMemoryCachingStore
{
    private readonly IMemoryCachingStore _tier1; // Memory
    private readonly IMemoryCachingStore _tier2; // Sqlite
    private readonly IMemoryCachingStore _tier3; // Null
    private bool _disposed;

    public string ActiveTier
    {
        get
        {
            if (_tier1.CheckpointCount > 0) return _tier1.ActiveTier;
            if (_tier2.CheckpointCount > 0) return _tier2.ActiveTier;
            return _tier3.ActiveTier;
        }
    }

    public int CheckpointCount => _tier1.CheckpointCount + _tier2.CheckpointCount;

    public CachingCascade(
        IMemoryCachingStore? tier1 = null,
        IMemoryCachingStore? tier2 = null,
        IMemoryCachingStore? tier3 = null)
    {
        _tier1 = tier1 ?? new MemoryCachingStore();
        _tier2 = tier2 ?? new LazyCachingStore();
        _tier3 = tier3 ?? new NullCachingStore();
    }

    /// <summary>
    /// Internal: Lazy-initialized file store.
    /// The JSON file is created on first use, not at construction.
    /// </summary>
    private sealed class LazyCachingStore : IMemoryCachingStore
    {
        private readonly object _lock = new();
        private FileCachingStore? _inner;
        private bool _initAttempted;

        public string ActiveTier => _inner?.ActiveTier ?? "File(Lazy)";
        public int CheckpointCount => _inner?.CheckpointCount ?? 0;

        public Task StoreAsync(string key, byte[] data, long tokenCount, CancellationToken ct = default)
        {
            var store = GetOrInit();
            return store?.StoreAsync(key, data, tokenCount, ct) ?? Task.CompletedTask;
        }

        public Task<byte[]?> LookupAsync(string key, CancellationToken ct = default)
        {
            var store = GetOrInit();
            return store?.LookupAsync(key, ct) ?? Task.FromResult<byte[]?>(null);
        }

        public Task<(string key, byte[] data, long tokenCount)?> FindNearestAsync(
            string sessionId, long tokenCount, CancellationToken ct = default)
        {
            var store = GetOrInit();
            return store?.FindNearestAsync(sessionId, tokenCount, ct)
                   ?? Task.FromResult<(string, byte[], long)?>(null);
        }

        public Task<IReadOnlyList<CheckpointSummary>> FindRangeAsync(
            string sessionId, long fromToken, long toToken, CancellationToken ct = default)
        {
            var store = GetOrInit();
            return store?.FindRangeAsync(sessionId, fromToken, toToken, ct)
                   ?? Task.FromResult<IReadOnlyList<CheckpointSummary>>([]);
        }

        public Task InvalidateSessionAsync(string sessionId, CancellationToken ct = default)
        {
            var store = GetOrInit();
            return store?.InvalidateSessionAsync(sessionId, ct) ?? Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken ct = default)
        {
            var store = GetOrInit();
            return store?.ClearAsync(ct) ?? Task.CompletedTask;
        }

        public void Dispose()
        {
            _inner?.Dispose();
        }

        private FileCachingStore? GetOrInit()
        {
            if (_inner != null) return _inner;
            if (_initAttempted) return null;

            lock (_lock)
            {
                if (_inner != null) return _inner;
                if (_initAttempted) return null;
                _initAttempted = true;

                try
                {
                    var dataDir = Path.Combine(
                        AppContext.BaseDirectory,
                        ".livingtree");
                    return new FileCachingStore(dataDir);
                }
                catch
                {
                    return null;
                }
            }
        }
    }

    // ═══════════════════════════════════════════
    //  Write-through: all tiers
    // ═══════════════════════════════════════════

    public async Task StoreAsync(string key, byte[] data, long tokenCount, CancellationToken ct = default)
    {
        await _tier1.StoreAsync(key, data, tokenCount, ct).ConfigureAwait(false);
        await _tier2.StoreAsync(key, data, tokenCount, ct).ConfigureAwait(false);
        // Tier 3 (Null) is no-op
    }

    // ═══════════════════════════════════════════
    //  Read-through: Tier 1 → Tier 2 → Tier 3
    // ═══════════════════════════════════════════

    public async Task<byte[]?> LookupAsync(string key, CancellationToken ct = default)
    {
        var result = await _tier1.LookupAsync(key, ct).ConfigureAwait(false);
        if (result != null) return result;

        result = await _tier2.LookupAsync(key, ct).ConfigureAwait(false);
        return result;
    }

    public async Task<(string key, byte[] data, long tokenCount)?> FindNearestAsync(
        string sessionId, long tokenCount, CancellationToken ct = default)
    {
        var result = await _tier1.FindNearestAsync(sessionId, tokenCount, ct).ConfigureAwait(false);
        if (result != null) return result;

        result = await _tier2.FindNearestAsync(sessionId, tokenCount, ct).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<CheckpointSummary>> FindRangeAsync(
        string sessionId, long fromToken, long toToken, CancellationToken ct = default)
    {
        var results = new List<CheckpointSummary>();

        var tier1Results = await _tier1.FindRangeAsync(sessionId, fromToken, toToken, ct).ConfigureAwait(false);
        results.AddRange(tier1Results);

        var tier2Results = await _tier2.FindRangeAsync(sessionId, fromToken, toToken, ct).ConfigureAwait(false);
        results.AddRange(tier2Results);

        return results.OrderBy(s => s.TokenCount).ToList();
    }

    public async Task InvalidateSessionAsync(string sessionId, CancellationToken ct = default)
    {
        await _tier1.InvalidateSessionAsync(sessionId, ct).ConfigureAwait(false);
        await _tier2.InvalidateSessionAsync(sessionId, ct).ConfigureAwait(false);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _tier1.ClearAsync(ct).ConfigureAwait(false);
        await _tier2.ClearAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tier1.Dispose();
        _tier2.Dispose();
        _tier3.Dispose();
    }
}
