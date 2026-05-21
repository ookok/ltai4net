using System.Diagnostics;
using System.Runtime.CompilerServices;
using LTAI.Execution.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Execution.Modes;

public sealed class BatchTask : IComparable<BatchTask>
{
    public string Name { get; set; } = "";
    public Func<CancellationToken, Task<object?>>? Handler { get; set; }
    public int Priority { get; set; }
    public Dictionary<string, object?> Metadata { get; set; } = new();
    public double CreatedAt { get; set; }
    public object? Result { get; set; }
    public string? Error { get; set; }
    public string Status { get; set; } = "pending";
    public double StartedAt { get; set; }
    public double CompletedAt { get; set; }

    public double LatencyMs => (CompletedAt - StartedAt) * 1000;

    public int CompareTo(BatchTask? other)
    {
        if (other is null) return 1;

        var cmp = (-Priority).CompareTo(-other.Priority);
        if (cmp != 0) return cmp;

        return CreatedAt.CompareTo(other.CreatedAt);
    }
}

public sealed class BatchExecutor
{
    private readonly List<BatchTask> _deque = new();
    private readonly ILogger<BatchExecutor> _logger;
    private readonly object _lock = new();
    private BatchMode _mode;

    public BatchExecutor(BatchMode mode = BatchMode.FIFO, ILogger<BatchExecutor>? logger = null)
    {
        _mode = mode;
        _logger = logger ?? NullLogger.Instance;
    }

    public BatchTask Enqueue(BatchTask task)
    {
        task.CreatedAt = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        lock (_lock)
        {
            _deque.Add(task);
            SortQueue();
        }
        return task;
    }

    public List<BatchTask> EnqueueBatch(List<BatchTask> tasks)
    {
        var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        foreach (var task in tasks)
            task.CreatedAt = now;

        lock (_lock)
        {
            _deque.AddRange(tasks);
            SortQueue();
        }
        return tasks;
    }

    public BatchTask? NextTask()
    {
        lock (_lock)
        {
            if (_deque.Count == 0) return null;

            return _mode switch
            {
                BatchMode.LIFO => _deque[^1],
                _ => _deque[0]
            };
        }
    }

    public async Task<BatchTask?> ExecuteNext(CancellationToken ct = default)
    {
        BatchTask? task;
        lock (_lock)
        {
            if (_deque.Count == 0) return null;

            if (_mode == BatchMode.LIFO)
            {
                task = _deque[^1];
                _deque.RemoveAt(_deque.Count - 1);
            }
            else
            {
                task = _deque[0];
                _deque.RemoveAt(0);
            }
        }

        return await ExecuteTask(task, ct);
    }

    public async Task<List<BatchTask>> ExecuteAll(
        BatchMode? mode = null,
        CancellationToken ct = default)
    {
        var results = new List<BatchTask>();
        var overrideMode = mode;
        var originalMode = _mode;

        try
        {
            if (overrideMode.HasValue)
                _mode = overrideMode.Value;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var result = await ExecuteNext(ct);
                if (result != null)
                    results.Add(result);
                else
                    break;
            }
        }
        finally
        {
            if (overrideMode.HasValue)
                _mode = originalMode;
        }

        return results;
    }

    public async IAsyncEnumerable<BatchTask> ExecuteGenerator(
        BatchMode? mode = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (mode.HasValue)
            _mode = mode.Value;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var result = await ExecuteNext(ct);
            if (result != null)
                yield return result;
            else
                break;
        }
    }

    public void Clear() { lock (_lock) _deque.Clear(); }

    public Dictionary<string, object?> GetStats()
    {
        lock (_lock)
        {
            var completed = _deque.Where(t => t.Status == "completed").ToList();
            var failed = _deque.Where(t => t.Status == "failed").ToList();

            return new Dictionary<string, object?>
            {
                ["total"] = _deque.Count,
                ["pending"] = _deque.Count(t => t.Status == "pending"),
                ["running"] = _deque.Count(t => t.Status == "running"),
                ["completed"] = completed.Count,
                ["failed"] = failed.Count,
                ["mode"] = _mode.ToString(),
                ["avg_latency_ms"] = completed.Count > 0
                    ? Math.Round(completed.Average(t => t.LatencyMs), 1)
                    : 0,
                ["max_latency_ms"] = completed.Count > 0
                    ? Math.Round(completed.Max(t => t.LatencyMs), 1)
                    : 0
            };
        }
    }

    public List<BatchTask> PendingTasks()
    {
        lock (_lock)
            return _deque.Where(t => t.Status == "pending").ToList();
    }

    private void SortQueue()
    {
        _deque.Sort();
    }

    private async Task<BatchTask> ExecuteTask(BatchTask task, CancellationToken ct)
    {
        task.Status = "running";
        task.StartedAt = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        _logger.LogDebug("Batch executing: {Name} (priority={Priority})", task.Name, task.Priority);

        try
        {
            if (task.Handler != null)
            {
                task.Result = await task.Handler(ct);
                task.Status = "completed";
            }
            else
            {
                task.Status = "completed";
                task.Result = null;
            }
        }
        catch (Exception ex)
        {
            task.Error = ex.Message;
            task.Status = "failed";
            _logger.LogError(ex, "Batch task {Name} failed", task.Name);
        }
        finally
        {
            task.CompletedAt = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        }

        return task;
    }

    public static BatchExecutor CreateBatchExecutor(string mode = "fifo")
    {
        var batchMode = string.Equals(mode, "lifo", StringComparison.OrdinalIgnoreCase)
            ? BatchMode.LIFO
            : BatchMode.FIFO;

        return new BatchExecutor(batchMode);
    }

    private sealed class NullLogger : ILogger<BatchExecutor>
    {
        public static readonly NullLogger Instance = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
