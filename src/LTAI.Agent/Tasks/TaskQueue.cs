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
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Tasks;

public enum TaskStatus { Pending, Running, Completed, Failed, Cancelled }

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
    private readonly Channel<TaskItem> _channel;
    private readonly ITaskStore _store;
    private readonly ConcurrentDictionary<string, TaskItem> _items = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task[] _consumers;
    private readonly int _maxConcurrency;
    private readonly ILogger<TaskQueue>? _logger;

    public event Action<TaskItem>? TaskCompleted;

    public TaskQueue(ITaskStore? store = null, int maxConcurrency = 4,
        ILogger<TaskQueue>? logger = null)
    {
        _store = store ?? new InMemoryTaskStore();
        _maxConcurrency = Math.Max(1, maxConcurrency);
        _logger = logger;
        _channel = Channel.CreateUnbounded<TaskItem>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });
        _consumers = new Task[_maxConcurrency];
        for (int i = 0; i < _maxConcurrency; i++)
            _consumers[i] = Task.Run(() => ConsumerLoopAsync(_cts.Token));
    }

    public IReadOnlyList<TaskItem> List() => _items.Values.OrderByDescending(i => i.EnqueuedAt).ToList();

    public TaskItem? Get(string id) => _items.TryGetValue(id, out var item) ? item : null;

    /// <summary>
    /// Enqueue a work item. The returned TaskItem has a unique id; the
    /// actual execution happens on the consumer loop and can be polled
    /// via <see cref="Get"/> or waited via <see cref="WaitAsync(string)"/>.
    /// </summary>
    public async Task<TaskItem> EnqueueAsync(
        string name,
        Func<CancellationToken, Task<string>> work,
        string? description = null,
        CancellationToken ct = default)
    {
        var item = new TaskItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Description = description,
            Work = work,
        };
        _items[item.Id] = item;
        await _store.SaveAsync(item, ct).ConfigureAwait(false);
        await _channel.Writer.WriteAsync(item, ct).ConfigureAwait(false);
        _logger?.LogInformation("TaskQueue: enqueued {Id} '{Name}'", item.Id, item.Name);
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
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
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
        _logger?.LogInformation("TaskQueue: starting {Id} '{Name}' (attempt {Attempt})",
            item.Id, item.Name, item.Attempt);

        try
        {
            item.Result = item.Work != null
                ? await item.Work(ct).ConfigureAwait(false)
                : "(no work delegate)";
            item.Status = TaskStatus.Completed;
            _logger?.LogInformation("TaskQueue: completed {Id} '{Name}'", item.Id, item.Name);
        }
        catch (OperationCanceledException) { item.Status = TaskStatus.Cancelled; item.Error = "cancelled"; }
        catch (Exception ex)
        {
            item.Status = TaskStatus.Failed;
            item.Error = ex.Message;
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try { await Task.WhenAll(_consumers).ConfigureAwait(false); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
