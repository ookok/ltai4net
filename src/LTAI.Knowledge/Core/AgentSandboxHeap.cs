using System.Collections.Concurrent;

namespace LTAI.Knowledge.Core;

public sealed class AgentHeapStats
{
    public int Allocations { get; set; }
    public int Deallocations { get; set; }
    public int PeakBlockCount { get; set; }
    public int CurrentBlockCount { get; set; }
    public long PeakBytes { get; set; }
    public long CurrentBytes { get; set; }
    public long TotalBytesAllocated { get; set; }
    public long TotalBytesReleased { get; set; }
    public int BulkReleaseCount { get; set; }

    public double FragmentationRatio =>
        PeakBlockCount > 0
            ? 1.0 - (double)CurrentBlockCount / PeakBlockCount
            : 0;
}

public sealed class AgentSandboxHeap : IDisposable
{
    private readonly string _heapId;
    private readonly ConcurrentDictionary<string, HeapBlock> _blocks = new();
    private readonly object _gcLock = new();
    private readonly int _maxBlocks;
    private volatile bool _disposed;

    public string HeapId => _heapId;
    public AgentHeapStats Stats { get; } = new();

    public event Action<AgentSandboxHeap>? OnHeapFull;
    public event Action<List<HeapBlock>>? OnBulkRelease;

    public AgentSandboxHeap(string heapId, int maxBlocks = 50_000)
    {
        _heapId = heapId;
        _maxBlocks = maxBlocks;
    }

    public bool Allocate(string blockId, string content, double priority = 1.0)
    {
        if (_disposed)
            return false;

        lock (_gcLock)
        {
            if (Stats.CurrentBlockCount >= _maxBlocks)
            {
                OnHeapFull?.Invoke(this);
                if (Stats.CurrentBlockCount >= _maxBlocks)
                    return false;
            }

            var block = new HeapBlock
            {
                BlockId = blockId,
                Content = content,
                HeapId = _heapId,
                Priority = priority,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ByteSize = content.Length * 2
            };

            _blocks[blockId] = block;

            Stats.Allocations++;
            Stats.CurrentBlockCount = _blocks.Count;
            Stats.CurrentBytes += block.ByteSize;
            Stats.TotalBytesAllocated += block.ByteSize;

            if (Stats.CurrentBlockCount > Stats.PeakBlockCount)
                Stats.PeakBlockCount = Stats.CurrentBlockCount;

            if (Stats.CurrentBytes > Stats.PeakBytes)
                Stats.PeakBytes = Stats.CurrentBytes;

            return true;
        }
    }

    public bool Deallocate(string blockId)
    {
        if (!_blocks.TryRemove(blockId, out var block))
            return false;

        Stats.Deallocations++;
        Stats.CurrentBlockCount = _blocks.Count;
        Stats.CurrentBytes -= block.ByteSize;
        Stats.TotalBytesReleased += block.ByteSize;
        return true;
    }

    public HeapBlock? Read(string blockId)
    {
        _blocks.TryGetValue(blockId, out var block);
        if (block != null)
            block.LastAccess = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return block;
    }

    public List<HeapBlock> Query(
        Func<HeapBlock, bool>? filter = null,
        int topK = 20)
    {
        var query = filter != null
            ? _blocks.Values.Where(filter)
            : _blocks.Values;

        return query
            .OrderByDescending(b => b.Priority)
            .ThenByDescending(b => b.LastAccess)
            .Take(topK)
            .ToList();
    }

    public List<HeapBlock> BulkRelease()
    {
        var released = _blocks.Values.ToList();
        _blocks.Clear();

        Stats.CurrentBlockCount = 0;
        Stats.CurrentBytes = 0;
        Stats.BulkReleaseCount++;
        Stats.TotalBytesReleased += released.Sum(b => b.ByteSize);
        Stats.Deallocations += released.Count;

        OnBulkRelease?.Invoke(released);
        return released;
    }

    public bool Compact(Func<HeapBlock, bool> keepPredicate)
    {
        var toRemove = _blocks.Values
            .Where(b => !keepPredicate(b))
            .Select(b => b.BlockId)
            .ToList();

        foreach (var id in toRemove)
            Deallocate(id);

        return toRemove.Count > 0;
    }

    public void Dispose()
    {
        _disposed = true;
        BulkRelease();
    }

    public Dictionary<string, object> GetStats() => new()
    {
        ["heap_id"] = _heapId,
        ["allocations"] = Stats.Allocations,
        ["deallocations"] = Stats.Deallocations,
        ["current_blocks"] = Stats.CurrentBlockCount,
        ["peak_blocks"] = Stats.PeakBlockCount,
        ["current_bytes"] = Stats.CurrentBytes,
        ["peak_bytes"] = Stats.PeakBytes,
        ["total_allocated_bytes"] = Stats.TotalBytesAllocated,
        ["total_released_bytes"] = Stats.TotalBytesReleased,
        ["bulk_releases"] = Stats.BulkReleaseCount,
        ["fragmentation"] = Math.Round(Stats.FragmentationRatio, 3),
        ["disposed"] = _disposed
    };
}

public sealed class HeapBlock
{
    public string BlockId { get; set; } = "";
    public string Content { get; set; } = "";
    public string HeapId { get; set; } = "";
    public double Priority { get; set; } = 1.0;
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public long LastAccess { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public long ByteSize { get; set; }
    public Dictionary<string, string> Tags { get; set; } = new();
}

public sealed class AgentHeapManager
{
    private readonly ConcurrentDictionary<string, AgentSandboxHeap> _heaps = new();
    private readonly int _defaultMaxBlocks;
    private readonly object _lock = new();

    public AgentHeapManager(int defaultMaxBlocks = 50_000)
    {
        _defaultMaxBlocks = defaultMaxBlocks;
    }

    public AgentSandboxHeap GetOrCreateHeap(string heapId, int? maxBlocks = null)
    {
        return _heaps.GetOrAdd(heapId, _ =>
            new AgentSandboxHeap(heapId, maxBlocks ?? _defaultMaxBlocks));
    }

    public AgentSandboxHeap? GetHeap(string heapId)
    {
        _heaps.TryGetValue(heapId, out var heap);
        return heap;
    }

    public bool DestroyHeap(string heapId)
    {
        if (!_heaps.TryRemove(heapId, out var heap))
            return false;

        heap.Dispose();
        return true;
    }

    public List<HeapBlock> QueryAcrossHeaps(
        Func<HeapBlock, bool>? filter = null,
        int topK = 30)
    {
        var allBlocks = new List<HeapBlock>();

        foreach (var heap in _heaps.Values)
        {
            if (heap.Stats.CurrentBlockCount == 0)
                continue;

            var blocks = heap.Query(filter, topK: topK / Math.Max(1, _heaps.Count));
            allBlocks.AddRange(blocks);
        }

        return allBlocks
            .OrderByDescending(b => b.Priority)
            .ThenByDescending(b => b.LastAccess)
            .Take(topK)
            .ToList();
    }

    public void CompactAll(Func<HeapBlock, bool> keepPredicate)
    {
        foreach (var heap in _heaps.Values)
            heap.Compact(keepPredicate);
    }

    public Dictionary<string, object> GetManagerStats()
    {
        var heapStats = new Dictionary<string, object>();
        long totalBlocks = 0, totalBytes = 0;

        foreach (var (id, heap) in _heaps)
        {
            heapStats[id] = heap.GetStats();
            totalBlocks += heap.Stats.CurrentBlockCount;
            totalBytes += heap.Stats.CurrentBytes;
        }

        return new()
        {
            ["heap_count"] = _heaps.Count,
            ["total_blocks"] = totalBlocks,
            ["total_bytes"] = totalBytes,
            ["heaps"] = heapStats
        };
    }
}
