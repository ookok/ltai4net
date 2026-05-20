using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.MAF.Subagents;

public enum SubagentTaskState { Queued, Running, Completed, Failed, Canceled }

public sealed class SubagentTask
{
    public string TaskId { get; init; } = "";
    public string Description { get; init; } = "";
    public string SubagentName { get; init; } = "";
    public SubagentTaskState State { get; set; } = SubagentTaskState.Queued;
    public string? Result { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public CancellationTokenSource? Cancellation { get; set; }
}

public delegate Task<string?> SubagentHandler(string taskDescription, CancellationToken ct);

public sealed class BackgroundSubagentRunner
{
    private readonly ConcurrentDictionary<string, SubagentTask> _tasks = new();
    private readonly ConcurrentDictionary<string, SubagentHandler> _handlers = new();
    private readonly ILogger<BackgroundSubagentRunner> _logger;
    private int _taskCounter;

    public BackgroundSubagentRunner(ILogger<BackgroundSubagentRunner>? logger = null)
    {
        _logger = logger ?? NullLogger<BackgroundSubagentRunner>.Instance;
    }

    public void RegisterSubagent(string name, SubagentHandler handler)
    {
        _handlers[name] = handler;
        _logger.LogInformation("Registered subagent: {Name}", name);
    }

    public string RunTask(string subagentName, string description)
    {
        var taskId = $"Task-{Interlocked.Increment(ref _taskCounter)}";
        var task = new SubagentTask
        {
            TaskId = taskId,
            Description = description,
            SubagentName = subagentName,
            State = SubagentTaskState.Queued,
            Cancellation = new CancellationTokenSource()
        };
        _tasks[taskId] = task;

        _ = ExecuteAsync(task);
        return taskId;
    }

    private async Task ExecuteAsync(SubagentTask task)
    {
        if (!_handlers.TryGetValue(task.SubagentName, out var handler))
        {
            task.State = SubagentTaskState.Failed;
            task.Error = $"Unknown subagent: {task.SubagentName}";
            task.CompletedAt = DateTime.UtcNow;
            return;
        }

        task.State = SubagentTaskState.Running;
        task.StartedAt = DateTime.UtcNow;

        try
        {
            var result = await handler(task.Description, task.Cancellation?.Token ?? CancellationToken.None);

            if (task.Cancellation?.IsCancellationRequested == true)
            {
                task.State = SubagentTaskState.Canceled;
                task.Error = "Canceled";
            }
            else
            {
                task.State = SubagentTaskState.Completed;
                task.Result = result;
            }
        }
        catch (Exception ex)
        {
            task.State = SubagentTaskState.Failed;
            task.Error = ex.Message;
            _logger.LogWarning(ex, "Subagent task failed: {TaskId} {Subagent}", task.TaskId, task.SubagentName);
        }
        finally
        {
            task.CompletedAt = DateTime.UtcNow;
            task.Cancellation?.Dispose();
            task.Cancellation = null;
        }
    }

    public string? TaskOutput(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
            return $"Error: Task {taskId} not found";

        return task.State switch
        {
            SubagentTaskState.Completed => task.Result ?? "(empty)",
            SubagentTaskState.Failed => $"Error: {task.Error ?? "Unknown error"}",
            SubagentTaskState.Canceled => "Canceled",
            SubagentTaskState.Running => $"Still running (started {task.StartedAt?.ToString("HH:mm:ss")})",
            SubagentTaskState.Queued => $"Queued",
            _ => "Unknown state"
        };
    }

    public async Task<Dictionary<string, string>> WaitAsync(int? taskNumber = null, TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromMinutes(5);

        if (taskNumber.HasValue)
        {
            var taskId = $"Task-{taskNumber.Value}";
            return await WaitForTaskAsync(taskId, timeout.Value);
        }

        var allTasks = _tasks.Values
            .Where(t => t.State is SubagentTaskState.Queued or SubagentTaskState.Running)
            .ToList();

        var results = new Dictionary<string, string>();
        foreach (var task in allTasks)
        {
            var result = await WaitForTaskInternalAsync(task, timeout.Value);
            results[task.TaskId] = result;
        }

        return results;
    }

    private async Task<Dictionary<string, string>> WaitForTaskAsync(string taskId, TimeSpan timeout)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
            return new() { [taskId] = "Task not found" };

        var result = await WaitForTaskInternalAsync(task, timeout);
        return new() { [taskId] = result };
    }

    private async Task<string> WaitForTaskInternalAsync(SubagentTask task, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (task.State is SubagentTaskState.Queued or SubagentTaskState.Running)
        {
            if (DateTime.UtcNow > deadline)
                return $"Timeout after {timeout.TotalSeconds:F0}s";

            await Task.Delay(200);
        }

        return task.State switch
        {
            SubagentTaskState.Completed => task.Result ?? "(empty)",
            SubagentTaskState.Failed => $"Error: {task.Error}",
            SubagentTaskState.Canceled => "Canceled",
            _ => task.Result ?? ""
        };
    }

    public bool CancelTask(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task)) return false;
        if (task.State is not (SubagentTaskState.Running or SubagentTaskState.Queued)) return false;

        task.Cancellation?.Cancel();
        task.State = SubagentTaskState.Canceled;
        task.CompletedAt = DateTime.UtcNow;
        return true;
    }

    public List<SubagentTask> GetAllTasks()
        => _tasks.Values.OrderBy(t => t.CreatedAt).ToList();

    public List<SubagentTask> GetActiveTasks()
        => _tasks.Values
            .Where(t => t.State is SubagentTaskState.Queued or SubagentTaskState.Running)
            .OrderBy(t => t.CreatedAt).ToList();

    public string GetTasksSummary()
    {
        var tasks = GetAllTasks();
        if (tasks.Count == 0) return "No tasks.";

        return string.Join("\n", tasks.Select(t =>
        {
            var state = t.State switch
            {
                SubagentTaskState.Queued => "⏳",
                SubagentTaskState.Running => "🔄",
                SubagentTaskState.Completed => "✅",
                SubagentTaskState.Failed => "❌",
                SubagentTaskState.Canceled => "🚫",
                _ => "❓"
            };
            return $"{state} {t.TaskId} [{t.SubagentName}]: {t.Description[..Math.Min(t.Description.Length, 80)]}";
        }));
    }
}
