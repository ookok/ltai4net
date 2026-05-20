using System.Collections.Concurrent;

namespace LTAI.Web;

public sealed record BufferSlot
{
    public string RequestId { get; init; } = "";
    public DateTime EnqueuedAt { get; init; } = DateTime.UtcNow;
    public int Priority { get; init; }
    public Dictionary<string, object> Context { get; init; } = new();
}

public sealed class BufferStats
{
    public int QueueDepth { get; set; }
    public int MaxDepth { get; set; }
    public int TotalEnqueued { get; set; }
    public int TotalDequeued { get; set; }
    public int TotalRejected { get; set; }
    public double AvgWaitMs { get; set; }
    public double CurrentPressure { get; set; }
}

public sealed class RequestBuffer
{
    private static readonly Lazy<RequestBuffer> _instance = new(() => new RequestBuffer());
    public static RequestBuffer Instance => _instance.Value;

    private readonly int _maxQueue;
    private readonly double _highWatermark;
    private readonly double _criticalWatermark;
    private readonly double _maxWaitSeconds;
    private readonly ConcurrentQueue<BufferSlot> _queue = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _events = new();
    private readonly BufferStats _stats = new();
    private readonly object _lock = new();
    private int _queueCount;

    private RequestBuffer(int maxQueue = 1000, double highWatermark = 0.80,
        double criticalWatermark = 0.95, double maxWaitSeconds = 60.0)
    {
        _maxQueue = maxQueue;
        _highWatermark = highWatermark;
        _criticalWatermark = criticalWatermark;
        _maxWaitSeconds = maxWaitSeconds;
    }

    public bool TryEnqueue(string requestId, int priority = 0)
    {
        lock (_lock)
        {
            var ratio = (double)_queueCount / _maxQueue;

            if (ratio >= _criticalWatermark)
            {
                _stats.TotalRejected++;
                return false;
            }

            var slot = new BufferSlot { RequestId = requestId, Priority = priority };
            _queue.Enqueue(slot);
            _queueCount++;
            _stats.TotalEnqueued++;
            _stats.QueueDepth = _queueCount;
            _stats.MaxDepth = Math.Max(_stats.MaxDepth, _queueCount);
            _stats.CurrentPressure = ratio;

            _events[requestId] = new TaskCompletionSource<bool>();
        }

        return true;
    }

    public async Task<string?> DequeueAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            lock (_lock)
            {
                if (_queue.TryDequeue(out var slot))
                {
                    _queueCount--;
                    _stats.TotalDequeued++;
                    _stats.QueueDepth = _queueCount;
                    _stats.CurrentPressure = (double)_queueCount / _maxQueue;

                    var waitMs = (DateTime.UtcNow - slot.EnqueuedAt).TotalMilliseconds;
                    const double alpha = 0.1;
                    _stats.AvgWaitMs = (1 - alpha) * _stats.AvgWaitMs + alpha * waitMs;

                    if (_events.TryRemove(slot.RequestId, out var tcs))
                        tcs.TrySetResult(true);

                    return slot.RequestId;
                }
            }

            await Task.Delay(50, cancellationToken);
        }

        return null;
    }

    public async Task<bool> WaitForTurnAsync(string requestId, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);

        if (!_events.TryGetValue(requestId, out var tcs))
            return true;

        var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(effectiveTimeout));
        return completedTask == tcs.Task && tcs.Task.Result;
    }

    public bool NeedsBackpressure()
    {
        var ratio = (double)_queueCount / Math.Max(1, _maxQueue);
        return ratio >= _highWatermark;
    }

    public int? RetryAfterHeader()
    {
        if (!NeedsBackpressure())
            return null;

        var rate = Math.Max(1, _stats.TotalDequeued);
        var wait = (int)(_queueCount / (double)rate * 10);
        return Math.Max(1, wait);
    }

    public BufferStats Stats => _stats;
}
