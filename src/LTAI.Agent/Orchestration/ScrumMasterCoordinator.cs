using System.Collections.Concurrent;
using LTAI.Agent.Tasks;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Orchestration;

public sealed record AgentTaskAssignment
{
    public string TaskId { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string Description { get; init; } = "";
    public string AssignedAgent { get; init; } = "";
    public string Status { get; set; } = "pending";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Result { get; set; }
    public string? BlockedReason { get; set; }
    public int Priority { get; init; }
}

[Obsolete("Registered in DI but not invoked by any code path. Agent LTAI-ScrumMaster uses BackgroundAgents instead.")]
public sealed class ScrumMasterCoordinator
{
    private readonly ConcurrentDictionary<string, AgentTaskAssignment> _tasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _agentWorkload = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ScrumMasterCoordinator> _logger;
    private readonly TaskQueue? _taskQueue;

    public IReadOnlyCollection<AgentTaskAssignment> Tasks => _tasks.Values.ToArray();
    public int PendingCount => _tasks.Values.Count(t => t.Status == "pending");
    public int InProgressCount => _tasks.Values.Count(t => t.Status == "in_progress");
    public int CompletedCount => _tasks.Values.Count(t => t.Status == "completed");

    public ScrumMasterCoordinator(ILogger<ScrumMasterCoordinator>? logger = null, TaskQueue? taskQueue = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ScrumMasterCoordinator>.Instance;
        _taskQueue = taskQueue;
    }

    public AgentTaskAssignment Assign(string description, string agent, int priority = 0)
    {
        var task = new AgentTaskAssignment
        {
            Description = description,
            AssignedAgent = agent,
            Priority = priority
        };
        _tasks[task.TaskId] = task;
        _agentWorkload.AddOrUpdate(agent, 1, (_, v) => v + 1);
        _logger.LogInformation("Assigned task {TaskId} to {Agent}: {Desc}", task.TaskId, agent, description);
        return task;
    }

    public bool TryStart(string taskId)
    {
        if (_tasks.TryGetValue(taskId, out var task) && task.Status == "pending")
        {
            task.Status = "in_progress";
            task.StartedAt = DateTime.UtcNow;
            return true;
        }
        return false;
    }

    public bool TryComplete(string taskId, string? result = null)
    {
        if (_tasks.TryGetValue(taskId, out var task) && task.Status == "in_progress")
        {
            task.Status = "completed";
            task.CompletedAt = DateTime.UtcNow;
            task.Result = result;
            if (!string.IsNullOrEmpty(task.AssignedAgent))
                _agentWorkload.AddOrUpdate(task.AssignedAgent, 0, (_, v) => Math.Max(0, v - 1));
            return true;
        }
        return false;
    }

    public bool TryBlock(string taskId, string reason)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            task.Status = "blocked";
            task.BlockedReason = reason;
            return true;
        }
        return false;
    }

    public int GetWorkload(string agent) => _agentWorkload.GetValueOrDefault(agent, 0);

    public string? GetNextTask(string agent)
    {
        return _tasks.Values
            .Where(t => t.Status == "pending" && t.AssignedAgent == agent)
            .OrderByDescending(t => t.Priority)
            .ThenBy(t => t.CreatedAt)
            .FirstOrDefault()?.TaskId;
    }

    public IReadOnlyList<AgentTaskAssignment> GetBlockedTasks()
        => _tasks.Values.Where(t => t.Status == "blocked").ToList();

    public string Summary()
    {
        var total = _tasks.Count;
        var pending = PendingCount;
        var inProg = InProgressCount;
        var completed = CompletedCount;
        var blocked = _tasks.Values.Count(t => t.Status == "blocked");
        return $"📋 Scrum Board: {total} tasks | {pending} pending | {inProg} in progress | {completed} done | {blocked} blocked";
    }

    public void Cleanup(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        foreach (var kv in _tasks)
        {
            if (kv.Value.Status == "completed" && kv.Value.CompletedAt < cutoff)
                _tasks.TryRemove(kv.Key, out _);
        }
    }
}
