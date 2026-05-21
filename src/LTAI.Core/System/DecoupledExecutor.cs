using System.Collections.Concurrent;
using LTAI.Core.Configuration;

namespace LTAI.Core.System;

public enum TaskStatusState
{
    Pending,
    Running,
    Done,
    Failed,
    Timeout
}

public sealed class TaskHandle
{
    public string TaskId { get; set; } = "";
    public TaskStatusState Status { get; set; } = TaskStatusState.Pending;
    public object? Result { get; set; }
    public string Error { get; set; } = "";
    public double StartedAt { get; set; }
    public double FinishedAt { get; set; }
    public int Retries { get; set; }
    public string WorkerId { get; set; } = "";
    public bool IsVirtual { get; set; }
}

public sealed class VirtualExperience
{
    public string EntityId { get; init; } = "";
    public string Action { get; init; } = "";
    public Dictionary<string, double> State { get; init; } = new();
    public Dictionary<string, double> NextState { get; init; } = new();
    public double Reward { get; init; }
    public double Confidence { get; init; }
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;
}

public sealed class DecoupledExecutor
{
    private static readonly Lazy<DecoupledExecutor> _instance = new(() => new DecoupledExecutor());
    public static DecoupledExecutor Instance => _instance.Value;

    private readonly int _maxConcurrent;
    private readonly TimeSpan _collectInterval;
    private readonly SemaphoreSlim _semaphore;
    private readonly ConcurrentDictionary<string, TaskHandle> _pending = new();
    private readonly ConcurrentQueue<TaskHandle> _results = new();
    private readonly ConcurrentQueue<VirtualExperience> _virtualExperiences = new();
    private readonly ConcurrentDictionary<string, Func<string[], Task<VirtualExperience?>>> _worldModels = new();
    private int _submitted;
    private int _completed;
    private int _failed;
    private int _timeout;

    private DecoupledExecutor(int maxConcurrent = 20, double collectIntervalSec = 0.1)
    {
        _maxConcurrent = maxConcurrent;
        _collectInterval = TimeSpan.FromSeconds(collectIntervalSec);
        _semaphore = new SemaphoreSlim(maxConcurrent);
    }

    public async Task<TaskHandle> SubmitAsync(
        Func<CancellationToken, Task> func, string taskId = "", string workerId = "",
        int retries = 0, TimeSpan? timeout = null)
    {
        var tid = string.IsNullOrEmpty(taskId) ? $"task_{Interlocked.Increment(ref _submitted)}" : taskId;
        var handle = new TaskHandle { TaskId = tid, WorkerId = workerId, Retries = retries };

        _pending.TryAdd(tid, handle);
        _ = RunAsync(handle, func, timeout ?? TimeSpan.FromSeconds(60));
        return await Task.FromResult(handle);
    }

    public async Task<TaskHandle> SubmitAsync(
        Func<CancellationToken, Task<object?>> func, string taskId = "", string workerId = "",
        int retries = 0, TimeSpan? timeout = null)
    {
        var tid = string.IsNullOrEmpty(taskId) ? $"task_{Interlocked.Increment(ref _submitted)}" : taskId;
        var handle = new TaskHandle { TaskId = tid, WorkerId = workerId, Retries = retries };

        _pending.TryAdd(tid, handle);
        _ = RunWithResultAsync(handle, func, timeout ?? TimeSpan.FromSeconds(60));
        return await Task.FromResult(handle);
    }

    public async Task<List<TaskHandle>> CollectAsync(TimeSpan? timeout = null, bool partial = true)
    {
        var results = new List<TaskHandle>();
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));

        while (DateTime.UtcNow < deadline)
        {
            while (_results.TryDequeue(out var handle))
                results.Add(handle);

            if (!partial && !_pending.IsEmpty)
            {
                await Task.Delay(_collectInterval);
                continue;
            }

            if (results.Count > 0 || _pending.IsEmpty)
                break;

            await Task.Delay(_collectInterval);
        }

        return results;
    }

    public int PendingCount => _pending.Count;

    public Dictionary<string, object> GetStats() => new()
    {
        ["pending"] = _pending.Count,
        ["submitted"] = _submitted,
        ["completed"] = _completed,
        ["failed"] = _failed,
        ["timeout"] = _timeout,
        ["max_concurrent"] = _maxConcurrent
    };

    private async Task RunAsync(TaskHandle handle, Func<CancellationToken, Task> func, TimeSpan timeout)
    {
        await _semaphore.WaitAsync();
        try
        {
            handle.Status = TaskStatusState.Running;
            handle.StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            for (var attempt = 0; attempt <= handle.Retries; attempt++)
            {
                try
                {
                    using var cts = new CancellationTokenSource(timeout);
                    await func(cts.Token);
                    handle.Status = TaskStatusState.Done;
                    Interlocked.Increment(ref _completed);
                    break;
                }
                catch (OperationCanceledException)
                {
                    if (attempt >= handle.Retries)
                    {
                        handle.Status = TaskStatusState.Timeout;
                        handle.Error = "timeout";
                        Interlocked.Increment(ref _timeout);
                    }
                }
                catch (Exception ex)
                {
                    if (attempt >= handle.Retries)
                    {
                        handle.Status = TaskStatusState.Failed;
                        handle.Error = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
                        Interlocked.Increment(ref _failed);
                    }
                }
            }

            handle.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _pending.TryRemove(handle.TaskId, out _);
            _results.Enqueue(handle);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task RunWithResultAsync(TaskHandle handle, Func<CancellationToken, Task<object?>> func, TimeSpan timeout)
    {
        await _semaphore.WaitAsync();
        try
        {
            handle.Status = TaskStatusState.Running;
            handle.StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            for (var attempt = 0; attempt <= handle.Retries; attempt++)
            {
                try
                {
                    using var cts = new CancellationTokenSource(timeout);
                    handle.Result = await func(cts.Token);
                    handle.Status = TaskStatusState.Done;
                    Interlocked.Increment(ref _completed);
                    break;
                }
                catch (OperationCanceledException)
                {
                    if (attempt >= handle.Retries)
                    {
                        handle.Status = TaskStatusState.Timeout;
                        handle.Error = "timeout";
                        Interlocked.Increment(ref _timeout);
                    }
                }
                catch (Exception ex)
                {
                    if (attempt >= handle.Retries)
                    {
                        handle.Status = TaskStatusState.Failed;
                        handle.Error = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
                        Interlocked.Increment(ref _failed);
                    }
                }
            }

            handle.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _pending.TryRemove(handle.TaskId, out _);
            _results.Enqueue(handle);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void RegisterWorldModel(string modelId, Func<string[], Task<VirtualExperience?>> generator)
    {
        _worldModels[modelId] = generator;
    }

    public void GenerateVirtualRollouts(string modelId, string[] entities, int count = 8)
    {
        if (!_worldModels.TryGetValue(modelId, out var generator)) return;

        for (int i = 0; i < count; i++)
        {
            var entitySubset = entities.Length <= 3
                ? entities
                : entities.OrderBy(_ => Random.Shared.Next()).Take(Math.Min(3, entities.Length)).ToArray();

            _ = Task.Run(async () =>
            {
                try
                {
                    var exp = await generator(entitySubset);
                    if (exp != null)
                        _virtualExperiences.Enqueue(exp);
                }
                catch { /* non-fatal */ }
            });
        }
    }

    public List<VirtualExperience> CollectVirtualExperiences(int maxCount = 50)
    {
        var results = new List<VirtualExperience>();
        while (results.Count < maxCount && _virtualExperiences.TryDequeue(out var exp))
            results.Add(exp);
        return results;
    }

    public async Task<List<TaskHandle>> SubmitVirtualRolloutsAsync(
        Func<CancellationToken, Task<object?>> func, int count = 4,
        TimeSpan? timeout = null)
    {
        var tasks = new List<TaskHandle>();
        for (int i = 0; i < count; i++)
        {
            var tid = $"virtual_{Interlocked.Increment(ref _submitted)}";
            var handle = new TaskHandle { TaskId = tid, WorkerId = "world-model", IsVirtual = true };
            _pending.TryAdd(tid, handle);
            _ = RunWithResultAsync(handle, func, timeout ?? TimeSpan.FromSeconds(30));
            tasks.Add(handle);
        }
        return tasks;
    }

    public (int submitted, int completed, int failed, int timeout, int pending, int virtualExp)
        GetExtendedStats() => (_submitted, _completed, _failed, _timeout, _pending.Count, _virtualExperiences.Count);
}
