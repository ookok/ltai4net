// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  MemoryWriteQueue — EvoEmbedding-inspired batch write buffer
//
//  Prevents HNSW representation collapse by batching vector inserts
//  instead of one-at-a-time writes. Uses segment-batching (arXiv:2606.21649)
//  to reduce embedding drift and improve write throughput.
//
//  Design:
//   - Accumulates (vector, drawerId) in a queue
//   - Flushes when: queue size ≥ batchSize OR timer fires
//   - Single HNSW lock acquisition per batch (not per write)
// ═══════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using LTAI.Agent.Vector;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

public sealed class MemoryWriteQueue : IDisposable
{
    private readonly HnswIndex _hnsw;
    private readonly ConcurrentDictionary<int, string> _hnswMap;
    private readonly ConcurrentDictionary<string, int> _hnswRev;
    private readonly SemaphoreSlim _hnswLock;
    private readonly ILogger? _logger;

    private readonly int _batchSize;
    private readonly int _flushIntervalMs;
    private readonly ConcurrentQueue<(float[] Vector, string DrawerId)> _queue = new();
    private readonly Timer _flushTimer;
    private int _queued;
    private bool _disposed;

    /// <summary>Default batch size before triggering a flush.</summary>
    public const int DefaultBatchSize = 32;

    /// <summary>Default flush interval in milliseconds.</summary>
    public const int DefaultFlushIntervalMs = 500;

    public int QueuedCount => _queued;

    public MemoryWriteQueue(
        HnswIndex hnsw,
        ConcurrentDictionary<int, string> hnswMap,
        ConcurrentDictionary<string, int> hnswRev,
        SemaphoreSlim hnswLock,
        ILogger? logger = null,
        int batchSize = DefaultBatchSize,
        int flushIntervalMs = DefaultFlushIntervalMs)
    {
        _hnsw = hnsw;
        _hnswMap = hnswMap;
        _hnswRev = hnswRev;
        _hnswLock = hnswLock;
        _logger = logger;
        _batchSize = batchSize;
        _flushIntervalMs = flushIntervalMs;
        _flushTimer = new Timer(OnTimerFlush, null, flushIntervalMs, flushIntervalMs);
    }

    /// <summary>Enqueue a vector for batch write. Non-blocking.</summary>
    public void Enqueue(float[] vector, string drawerId)
    {
        if (_disposed) return;
        _queue.Enqueue((vector, drawerId));
        var count = Interlocked.Increment(ref _queued);

        // High-water mark: flush immediately when batch is full
        if (count >= _batchSize)
        {
            Interlocked.Exchange(ref _queued, 0);
            _ = FlushBatchAsync();
        }
    }

    /// <summary>Force flush all pending vectors immediately.</summary>
    public async Task FlushAllAsync()
    {
        if (_disposed || _queue.IsEmpty) return;
        var snapshot = DequeueAll();
        if (snapshot.Count == 0) return;
        await WriteBatchAsync(snapshot).ConfigureAwait(false);
    }

    // ── Internal ──

    private void OnTimerFlush(object? state)
    {
        if (_disposed || _queue.IsEmpty) return;
        Interlocked.Exchange(ref _queued, 0);
        _ = FlushBatchAsync();
    }

    private async Task FlushBatchAsync()
    {
        try
        {
            var snapshot = DequeueAll();
            if (snapshot.Count == 0) return;
            await WriteBatchAsync(snapshot).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "MemoryWriteQueue: batch flush failed ({Count} items may be lost)", _queue.Count);
        }
    }

    private List<(float[] Vector, string DrawerId)> DequeueAll()
    {
        var snapshot = new List<(float[] Vector, string DrawerId)>();
        while (_queue.TryDequeue(out var item))
            snapshot.Add(item);
        return snapshot;
    }

    private async Task WriteBatchAsync(List<(float[] Vector, string DrawerId)> batch)
    {
        await _hnswLock.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var (vec, drawerId) in batch)
            {
                var idx = _hnsw.Insert(vec);
                _hnswMap[idx] = drawerId;
                _hnswRev[drawerId] = idx;
            }
            _logger?.LogDebug("MemoryWriteQueue: flushed {Count} vectors to HNSW", batch.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "MemoryWriteQueue: batch HNSW insert failed ({Count} items)", batch.Count);
            // Re-enqueue failed items for retry
            foreach (var item in batch)
                _queue.Enqueue(item);
        }
        finally
        {
            _hnswLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _flushTimer.Dispose();

        // Drain remaining items synchronously
        var remaining = DequeueAll();
        if (remaining.Count > 0)
        {
            _hnswLock.Wait();
            try
            {
                foreach (var (vec, drawerId) in remaining)
                {
                    var idx = _hnsw.Insert(vec);
                    _hnswMap[idx] = drawerId;
                    _hnswRev[drawerId] = idx;
                }
            }
            finally { _hnswLock.Release(); }
            _logger?.LogInformation("MemoryWriteQueue: disposed, drained {Count} remaining items", remaining.Count);
        }
    }
}
