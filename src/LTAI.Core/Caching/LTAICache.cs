using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace LTAI.Core.Caching;

public interface ILTAICacheStats
{
    long Hits { get; }
    long Misses { get; }
    long Evictions { get; }
    int Count { get; }
    double HitRate { get; }
    string MetricsSummary { get; }
}

public sealed class LTAICache<TKey, TValue> : IDisposable, ILTAICacheStats
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, CacheEntry> _store = new();
    private readonly LTAICacheOptions _options;
    private readonly LTAICacheMetrics _metrics = new();
    private readonly Timer _evictionTimer;
    private long _currentSizeBytes;
    private bool _disposed;

    public LTAICache(LTAICacheOptions? options = null)
    {
        _options = options ?? new LTAICacheOptions();
        _evictionTimer = new Timer(
            _ => SweepExpired(),
            null,
            _options.EvictionInterval,
            _options.EvictionInterval);
    }

    public long Hits => _metrics.Hits;
    public long Misses => _metrics.Misses;
    public long Evictions => _metrics.Evictions;
    public int Count => _store.Count;
    public double HitRate => _metrics.HitRate;
    public string MetricsSummary => _metrics.Summary;
    public LTAICacheMetrics Metrics => _metrics;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        if (_store.TryGetValue(key, out var entry) && !entry.IsExpired)
        {
            entry.Touch();
            _metrics.RecordHit();
            value = entry.Value;
            return true;
        }
        _metrics.RecordMiss();
        value = default;
        return false;
    }

    public void Set(TKey key, TValue value, TimeSpan? ttl = null)
    {
        var entry = new CacheEntry(value, ttl ?? _options.DefaultTtl);
        var size = EstimateSize(key, value);

        EvictIfNeeded(size);

        if (_store.TryGetValue(key, out var existing))
            Interlocked.Add(ref _currentSizeBytes, -existing.EstimatedSize);

        _store[key] = entry;
        entry.EstimatedSize = size;
        Interlocked.Add(ref _currentSizeBytes, size);
    }

    public bool Remove(TKey key)
    {
        if (_store.TryRemove(key, out var entry))
        {
            Interlocked.Add(ref _currentSizeBytes, -entry.EstimatedSize);
            return true;
        }
        return false;
    }

    public void Clear()
    {
        var count = _store.Count;
        _store.Clear();
        Interlocked.Exchange(ref _currentSizeBytes, 0);
        _metrics.Reset();
    }

    public async ValueTask<TValue> GetOrComputeAsync(
        TKey key,
        Func<TKey, Task<TValue>> factory,
        TimeSpan? ttl = null)
    {
        if (TryGet(key, out var cached))
            return cached;

        var value = await factory(key);
        Set(key, value, ttl);
        return value;
    }

    public int SweepExpired()
    {
        var now = Environment.TickCount64;
        var keys = _store
            .Where(kv => kv.Value.ExpiresAtTicks <= now)
            .Select(kv => kv.Key)
            .ToArray();

        var evicted = 0;
        foreach (var key in keys)
        {
            if (_store.TryRemove(key, out var entry))
            {
                Interlocked.Add(ref _currentSizeBytes, -entry.EstimatedSize);
                evicted++;
            }
        }

        if (evicted > 0)
            _metrics.RecordEvictions(evicted);

        return evicted;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _evictionTimer.Dispose();
        _store.Clear();
    }

    private void EvictIfNeeded(long newSize)
    {
        if (_options.MaxSizeBytes.HasValue)
        {
            while (_currentSizeBytes + newSize > _options.MaxSizeBytes.Value)
            {
                if (!EvictOneLru()) break;
            }
        }

        if (_options.MaxEntries.HasValue)
        {
            while (_store.Count >= _options.MaxEntries.Value)
            {
                if (!EvictOneLru()) break;
            }
        }
    }

    private bool EvictOneLru()
    {
        KeyValuePair<TKey, CacheEntry>? oldest = null;
        foreach (var kv in _store)
        {
            if (oldest == null || kv.Value.LastAccessTicks < oldest.Value.Value.LastAccessTicks)
                oldest = kv;
        }

        if (oldest == null) return false;

        if (_store.TryRemove(oldest.Value.Key, out var entry))
        {
            Interlocked.Add(ref _currentSizeBytes, -entry.EstimatedSize);
            _metrics.RecordEvictions(1);
            return true;
        }
        return false;
    }

    private static long EstimateSize(TKey key, TValue value)
    {
        long size = 64;
        if (key is string ks) size += ks.Length * 2;
        if (value is string vs) size += vs.Length * 2;
        else if (value is Array arr) size += arr.Length * 4;
        else if (value is float[] farr) size += farr.Length * 4;
        return size;
    }

    private sealed class CacheEntry
    {
        private volatile int _lastAccess;

        public TValue Value { get; }
        public long ExpiresAtTicks { get; }
        public long EstimatedSize { get; set; }
        public long LastAccessTicks => _lastAccess;
        public bool IsExpired => ExpiresAtTicks <= Environment.TickCount64;

        public CacheEntry(TValue value, TimeSpan ttl)
        {
            Value = value;
            ExpiresAtTicks = Environment.TickCount64 + (long)ttl.TotalMilliseconds;
            _lastAccess = unchecked((int)Environment.TickCount64);
        }

        public void Touch() => _lastAccess = unchecked((int)Environment.TickCount64);
    }
}
