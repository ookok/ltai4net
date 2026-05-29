using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace LTAI.Core.Acceleration;

public sealed record PoolStats
{
    public long Hits { get; set; }
    public long Misses { get; set; }
    public long BytesReclaimed { get; set; }
    public int PendingReturns { get; set; }
    public double HitRatio => (Hits + Misses) > 0 ? (double)Hits / (Hits + Misses) : 0;
}

public sealed class PooledJsonDocument : IDisposable
{
    private byte[] _buffer;
    private readonly int _bufferSize;
    private JsonDocument? _document;
    private readonly HotPathObjectPool _pool;
    private bool _disposed;

    internal PooledJsonDocument(byte[] buffer, int bufferSize, HotPathObjectPool pool)
    {
        _buffer = buffer;
        _bufferSize = bufferSize;
        _pool = pool;
    }

    public JsonDocument? Document
    {
        get
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PooledJsonDocument));
            if (_document == null && _buffer.Length > 0)
                _document = JsonDocument.Parse(_buffer);
            return _document;
        }
        set => _document = value;
    }

    public void LoadFrom(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > _bufferSize)
        {
            _pool.ReturnBuffer(_buffer, _bufferSize);
            _buffer = new byte[bytes.Length];
        }
        Array.Copy(bytes, _buffer, bytes.Length);
        _document?.Dispose();
        _document = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _document?.Dispose();
        _document = null;
        _pool.ReturnJsonDocument(this);
    }
}

/// <summary>
/// Object pool for hot-path JSON parsing and buffer reuse.
/// Reduces GC pressure by pooling byte buffers (10K/64K/256K slabs),
/// StringBuilder instances, and PooledJsonDocument wrappers.
/// Thread-safe via ConcurrentBag — no locks on hot path.
/// Callers: LTAI.Core.Acceleration.MemoryOptimizer, LTAI.AI.Providers.
/// Singleton via Instance property.
/// </summary>
public sealed class HotPathObjectPool
{
    private const int Size10K = 10 * 1024;
    private const int Size64K = 64 * 1024;
    private const int Size256K = 256 * 1024;
    private const int MaxPooledPerSlab = 32;

    private readonly ConcurrentBag<byte[]> _slab10K = new();
    private readonly ConcurrentBag<byte[]> _slab64K = new();
    private readonly ConcurrentBag<byte[]> _slab256K = new();
    private readonly ConcurrentBag<StringBuilder> _stringBuilders = new();
    private readonly ConcurrentBag<PooledJsonDocument> _jsonDocuments = new();

    private long _hits;
    private long _misses;
    private long _bytesReclaimed;

    public PoolStats GetStats() => new()
    {
        Hits = _hits,
        Misses = _misses,
        BytesReclaimed = _bytesReclaimed,
        PendingReturns = _slab10K.Count + _slab64K.Count + _slab256K.Count
            + _stringBuilders.Count + _jsonDocuments.Count
    };

    public byte[] RentBuffer(int minimumSize)
    {
        var slabSize = minimumSize <= Size10K ? Size10K
            : minimumSize <= Size64K ? Size64K
            : Size256K;

        var bag = slabSize switch
        {
            Size10K => _slab10K,
            Size64K => _slab64K,
            _ => _slab256K
        };

        if (bag.TryTake(out var buffer) && buffer.Length >= minimumSize)
        {
            Interlocked.Increment(ref _hits);
            return buffer;
        }

        Interlocked.Increment(ref _misses);
        return new byte[Math.Max(minimumSize, slabSize)];
    }

    public void ReturnBuffer(byte[] buffer, int slabSize)
    {
        if (buffer == null) return;

        var bag = slabSize switch
        {
            Size10K => _slab10K,
            Size64K => _slab64K,
            _ => _slab256K
        };

        if (bag.Count < MaxPooledPerSlab)
        {
            bag.Add(buffer);
            Interlocked.Add(ref _bytesReclaimed, buffer.Length);
        }
    }

    public StringBuilder RentStringBuilder()
    {
        if (_stringBuilders.TryTake(out var sb))
        {
            Interlocked.Increment(ref _hits);
            sb.Clear();
            return sb;
        }

        Interlocked.Increment(ref _misses);
        return new StringBuilder(1024);
    }

    public void ReturnStringBuilder(StringBuilder sb)
    {
        if (sb == null) return;
        if (sb.Capacity > 65536)
            return;

        if (_stringBuilders.Count < MaxPooledPerSlab)
        {
            sb.Clear();
            _stringBuilders.Add(sb);
            Interlocked.Add(ref _bytesReclaimed, sb.Capacity);
        }
    }

    public PooledJsonDocument RentJsonDocument(int expectedSize = Size10K)
    {
        if (_jsonDocuments.TryTake(out var doc))
        {
            Interlocked.Increment(ref _hits);
            return doc;
        }

        Interlocked.Increment(ref _misses);
        var buffer = new byte[expectedSize];
        return new PooledJsonDocument(buffer, expectedSize, this);
    }

    public async Task<string> RentStringBuilderAndBuildAsync(Func<StringBuilder, Task> buildAction)
    {
        var sb = RentStringBuilder();
        try
        {
            await buildAction(sb).ConfigureAwait(false);
            return sb.ToString();
        }
        finally
        {
            ReturnStringBuilder(sb);
        }
    }

    internal void ReturnJsonDocument(PooledJsonDocument doc)
    {
        if (_jsonDocuments.Count < MaxPooledPerSlab)
        {
            _jsonDocuments.Add(doc);
        }
    }
}
