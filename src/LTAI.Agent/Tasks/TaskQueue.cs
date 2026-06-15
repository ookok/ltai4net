// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  TaskQueue — In-process task queue with Channel<T> producer/consumer
//  semantics. Lightweight substitute for the MAF DurableTask package
//  (which requires Azure Functions hosting, not applicable to our
//  Desktop/TUI scenarios).
//
//  Scope:
//    - Single-process queue (no cross-machine distribution)
//    - Optional SQLite persistence (load pending tasks on startup)
//    - Background consumer hosted service (drains queue on a thread pool)
//    - In-memory task state tracking (Pending/Running/Completed/Failed/Cancelled)
//
//  NOT in scope:
//    - Cross-process/machine coordination
//    - Serverless cold-start resumption
//    - Workflow orchestration (use MAF Workflows for that)
//
//  Use cases:
//    - Defer long-running tool calls (e.g. large file analysis) to
//      free the LLM response loop
//    - Schedule recurring maintenance tasks
//    - Background work that survives only until process exit
// ═══════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Tasks;

public enum TaskStatus { Pending, Running, Completed, Failed, Cancelled }

public enum TaskPriority { Low = 0, Normal = 1, High = 2, Critical = 3 }

public sealed record TaskItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public DateTimeOffset EnqueuedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public TaskStatus Status { get; set; } = TaskStatus.Pending;
    public string? Result { get; set; }
    public string? Error { get; set; }
    public int Attempt { get; set; }
    public Func<CancellationToken, Task<string>>? Work { get; set; }
    public Action<double, string>? ReportProgress { get; set; }
    public double? Progress { get; set; }
    public string? ProgressMessage { get; set; }
    public TaskPriority Priority { get; init; } = TaskPriority.Normal;
}

public interface ITaskStore
{
    Task SaveAsync(TaskItem item, CancellationToken ct = default);
    Task UpdateAsync(TaskItem item, CancellationToken ct = default);
    Task<IReadOnlyList<TaskItem>> LoadPendingAsync(CancellationToken ct = default);
}

public sealed class InMemoryTaskStore : ITaskStore
{
    private readonly ConcurrentDictionary<string, TaskItem> _items = new();

    public Task SaveAsync(TaskItem item, CancellationToken ct = default)
    {
        _items[item.Id] = item;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(TaskItem item, CancellationToken ct = default)
    {
        _items[item.Id] = item;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TaskItem>> LoadPendingAsync(CancellationToken ct = default)
    {
        IReadOnlyList<TaskItem> pending = _items.Values
            .Where(i => i.Status is TaskStatus.Pending or TaskStatus.Running)
            .ToList();
        return Task.FromResult(pending);
    }
}

public sealed class TaskQueue : IAsyncDisposable
{
    private readonly Channel<TaskItem>[] _channels;
    private readonly ITaskStore _store;
    private readonly ConcurrentDictionary<string, TaskItem> _items = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task[] _consumers;
    private readonly int _maxConcurrency;
    private readonly int _maxRetries;
    private readonly TimeSpan _defaultTaskTimeout;
    private readonly ILogger<TaskQueue>? _logger;

    private long _enqueuedCount;
    private long _completedCount;
    private long _failedCount;
    private long _cancelledCount;
    private readonly ConcurrentQueue<TaskItem> _deadLetterQueue = new();
    private const int DeadLetterMaxSize = 1000;

    public long EnqueuedCount => Interlocked.Read(ref _enqueuedCount);
    public long CompletedCount => Interlocked.Read(ref _completedCount);
    public long FailedCount => Interlocked.Read(ref _failedCount);
    public long CancelledCount => Interlocked.Read(ref _cancelledCount);
    public int QueueDepth => _items.Count;
    public int ConsumerCount => _consumers.Length;
    public int DeadLetterCount => _deadLetterQueue.Count;
    public IReadOnlyList<TaskItem> DeadTasks => _deadLetterQueue.ToList();

    public event Action<TaskItem>? TaskCompleted;
    /// <summary>Fired when a task reports progress update.</summary>
    public event Action<TaskItem>? TaskProgress;

    private static readonly TaskPriority[] _priorityLevels = [TaskPriority.Low, TaskPriority.Normal, TaskPriority.High, TaskPriority.Critical];

    public TaskQueue(ITaskStore? store = null, int maxConcurrency = 4, int maxRetries = 3,
        ILogger<TaskQueue>? logger = null, TimeSpan? taskTimeout = null)
    {
        _store = store ?? new InMemoryTaskStore();
        _maxConcurrency = Math.Max(1, maxConcurrency);
        _maxRetries = Math.Max(0, maxRetries);
        _logger = logger;
        _defaultTaskTimeout = taskTimeout ?? TimeSpan.FromMinutes(10);
        var queueCap = int.TryParse(Environment.GetEnvironmentVariable("LTAI_TASK_QUEUE_MAX"), out var qc) ? Math.Max(100, qc) : -1;
        _channels = new Channel<TaskItem>[4];
        for (int i = 0; i < 4; i++)
        {
            var opts = new BoundedChannelOptions(queueCap > 0 ? queueCap : 100_000)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            };
            _channels[i] = Channel.CreateBounded<TaskItem>(opts);
        }
        _consumers = new Task[_maxConcurrency];
        for (int i = 0; i < _maxConcurrency; i++)
            _consumers[i] = Task.Run(() => ConsumerLoopAsync(_cts.Token));

        // Hydrate pending tasks from persistent store on startup
        _ = HydratePendingAsync();
    }

    private async Task HydratePendingAsync()
    {
        try
        {
            var pending = await _store.LoadPendingAsync(_cts.Token).ConfigureAwait(false);
            foreach (var item in pending)
            {
                if (item.Status is TaskStatus.Pending or TaskStatus.Running)
                {
                    _items[item.Id] = item;
                    var channelIdx = Math.Clamp((int)item.Priority, 0, 3);
                    await _channels[channelIdx].Writer.WriteAsync(item, _cts.Token).ConfigureAwait(false);
                    _logger?.LogInformation("TaskQueue: hydrated pending task {Id} '{Name}' from store", item.Id, item.Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "TaskQueue: failed to hydrate pending tasks from store");
        }
    }

    public IReadOnlyList<TaskItem> List() => _items.Values.OrderByDescending(i => i.EnqueuedAt).ToList();

    public TaskItem? Get(string id) => _items.TryGetValue(id, out var item) ? item : null;

    /// <summary>Get current progress for a task, or null if not found.</summary>
    public (double? Progress, string? Message)? GetProgress(string id)
    {
        if (_items.TryGetValue(id, out var item))
            return (item.Progress, item.ProgressMessage);
        return null;
    }

    /// <summary>
    /// Enqueue a work item. The returned TaskItem has a unique id; the
    /// actual execution happens on the consumer loop and can be polled
    /// via <see cref="Get"/> or waited via <see cref="WaitAsync(string)"/>.
    /// </summary>
    public async Task<TaskItem> EnqueueAsync(
        string name,
        Func<CancellationToken, Task<string>> work,
        string? description = null,
        CancellationToken ct = default,
        TaskPriority priority = TaskPriority.Normal)
    {
        var item = new TaskItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Description = description,
            Work = work,
            Priority = priority,
        };
        item.ReportProgress = (progress, msg) =>
        {
            item.Progress = progress;
            item.ProgressMessage = msg;
            TaskProgress?.Invoke(item);
        };
        _items[item.Id] = item;
        Interlocked.Increment(ref _enqueuedCount);
        await _store.SaveAsync(item, ct).ConfigureAwait(false);
        var channelIdx = Math.Clamp((int)priority, 0, 3);
        await _channels[channelIdx].Writer.WriteAsync(item, ct).ConfigureAwait(false);
        _logger?.LogInformation("TaskQueue: enqueued {Id} '{Name}' (priority={Priority})", item.Id, item.Name, priority);
        return item;
    }

    public async Task<string?> WaitAsync(string id, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromMinutes(10));
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_items.TryGetValue(id, out var item)
                && item.Status is TaskStatus.Completed or TaskStatus.Failed or TaskStatus.Cancelled)
                return item.Result ?? item.Error;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        return null;
    }

    private async Task ConsumerLoopAsync(CancellationToken ct)
    {
        var waitTasks = new Task<bool>[4];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TaskItem? item = null;
                for (int p = 3; p >= 0; p--)
                {
                    if (_channels[p].Reader.TryRead(out item))
                        break;
                }
                if (item == null)
                {
                    for (int i = 0; i < 4; i++)
                        waitTasks[i] = _channels[i].Reader.WaitToReadAsync(ct).AsTask();
                    var completed = await Task.WhenAny(waitTasks).ConfigureAwait(false);
                    if (completed.Result) continue;
                    break;
                }
                await RunOneAsync(item, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private async Task RunOneAsync(TaskItem item, CancellationToken ct)
    {
        item.Status = TaskStatus.Running;
        item.StartedAt = DateTimeOffset.UtcNow;
        item.Attempt++;
        await _store.UpdateAsync(item, ct).ConfigureAwait(false);
        _logger?.LogInformation("TaskQueue: starting {Id} '{Name}' (attempt {Attempt}, timeout={Timeout})",
            item.Id, item.Name, item.Attempt, _defaultTaskTimeout);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_defaultTaskTimeout);
        var linkedCt = timeoutCts.Token;

        try
        {
            item.Result = item.Work != null
                ? await item.Work(linkedCt).ConfigureAwait(false)
                : "(no work delegate)";
            item.Status = TaskStatus.Completed;
            Interlocked.Increment(ref _completedCount);
            _logger?.LogInformation("TaskQueue: completed {Id} '{Name}'", item.Id, item.Name);
        }
        catch (OperationCanceledException)
        {
            var isTimeout = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
            item.Status = isTimeout ? TaskStatus.Failed : TaskStatus.Cancelled;
            item.Error = isTimeout ? $"timeout ({_defaultTaskTimeout})" : "cancelled";
            if (isTimeout) Interlocked.Increment(ref _failedCount);
            else Interlocked.Increment(ref _cancelledCount);
        }
        catch (Exception ex)
        {
            item.Attempt++;
            if (item.Attempt <= _maxRetries)
            {
                item.Status = TaskStatus.Pending;
                item.Error = $"Retry {item.Attempt}/{_maxRetries}: {ex.Message}";
                _logger?.LogWarning(ex, "TaskQueue: retrying {Id} '{Name}' (attempt {Attempt}/{Max})",
                    item.Id, item.Name, item.Attempt, _maxRetries);
                await _store.UpdateAsync(item, CancellationToken.None).ConfigureAwait(false);
                var channelIdx = Math.Clamp((int)item.Priority, 0, 3);
                await _channels[channelIdx].Writer.WriteAsync(item, CancellationToken.None).ConfigureAwait(false);
                return; // Don't set CompletedAt — task is re-queued
            }

            item.Status = TaskStatus.Failed;
            item.Error = ex.Message;
            Interlocked.Increment(ref _failedCount);
            _deadLetterQueue.Enqueue(item); // preserve for inspection/replay
            if (_deadLetterQueue.Count > DeadLetterMaxSize)
                _deadLetterQueue.TryDequeue(out _); // evict oldest to cap memory
            _logger?.LogWarning(ex, "TaskQueue: failed {Id} '{Name}'", item.Id, item.Name);
        }
        finally
        {
            item.CompletedAt = DateTimeOffset.UtcNow;
            await _store.UpdateAsync(item, CancellationToken.None).ConfigureAwait(false);
            TaskCompleted?.Invoke(item);
        }
    }

    private volatile bool _disposed;

    /// <summary>Replay a dead-letter task by re-enqueuing it.</summary>
    public async Task<bool> ReplayDeadTaskAsync(string id, CancellationToken ct = default)
    {
        var dead = _deadLetterQueue.FirstOrDefault(t => t.Id == id);
        if (dead == null) return false;
        if (dead.Work == null)
        {
            _logger?.LogWarning("TaskQueue: cannot replay {Id} — no Work delegate (was deserialized from store)", id);
            return false;
        }
        dead.Status = TaskStatus.Pending;
        dead.Error = null;
        dead.Attempt = 0;
        _items[dead.Id] = dead;
        var channelIdx = Math.Clamp((int)dead.Priority, 0, 3);
        await _channels[channelIdx].Writer.WriteAsync(dead, ct).ConfigureAwait(false);
        // Note: item remains in dead letter queue for audit trail
        return true;
    }

    /// <summary>Purge dead-letter queue entries older than specified age.</summary>
    public int PurgeDeadTasks(TimeSpan? olderThan = null)
    {
        var cutoff = DateTimeOffset.UtcNow - (olderThan ?? TimeSpan.FromHours(24));
        var removed = 0;
        while (_deadLetterQueue.TryPeek(out var item) && item.CompletedAt < cutoff)
        {
            if (_deadLetterQueue.TryDequeue(out _)) removed++;
        }
        return removed;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var ch in _channels)
            ch.Writer.TryComplete();
        _cts.Cancel();
        try { await Task.WhenAll(_consumers).ConfigureAwait(false); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
