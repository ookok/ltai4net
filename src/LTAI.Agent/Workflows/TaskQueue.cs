using System.Collections.Concurrent;
using LTAI.Models;

namespace LTAI.Agent.Workflows;

public sealed class TaskQueue
{
    private readonly object _lock = new();
    private readonly Dictionary<string, CoordinatorTask> _tasks = new();
    private readonly Dictionary<string, int> _pendingCounts = new();
    private readonly Dictionary<string, List<string>> _dependents = new();
    private readonly BlockingCollection<string> _ready = new(new ConcurrentQueue<string>());

    private int _completedCount;
    private int _failedCount;
    private int _totalCount;

    public int CompletedCount => _completedCount;
    public int FailedCount => _failedCount;
    public int TotalCount => _totalCount;
    public bool AllDone => _completedCount + _failedCount >= _totalCount && _totalCount > 0;

    public int ReadyCount
    {
        get { lock (_lock) return _ready.Count; }
    }

    public void Enqueue(IReadOnlyList<CoordinatorTask> tasks)
    {
        if (HasCycle(tasks, out var cycleInfo))
            throw new InvalidOperationException(
                $"DAG cycle detected: {cycleInfo}. Tasks would deadlock.");

        lock (_lock)
        {
            _totalCount = tasks.Count;

            foreach (var t in tasks)
            {
                _tasks[t.Id] = t;
                _pendingCounts[t.Id] = t.DependsOn.Count;

                foreach (var dep in t.DependsOn)
                {
                    if (!_dependents.TryGetValue(dep, out var list))
                    {
                        list = new List<string>();
                        _dependents[dep] = list;
                    }
                    list.Add(t.Id);
                }
            }

            foreach (var t in tasks)
            {
                if (t.DependsOn.Count == 0)
                {
                    t.Status = CoordinatorTaskStatus.Ready;
                    _ready.Add(t.Id);
                }
            }
        }
    }

    /// <summary>Detect cycles in the task dependency graph using DFS.</summary>
    private static bool HasCycle(IReadOnlyList<CoordinatorTask> tasks, out string cycleInfo)
    {
        // Build adjacency: taskId → list of dependency IDs
        var adjacency = tasks.ToDictionary(t => t.Id, t => t.DependsOn);
        var allIds = new HashSet<string>(tasks.Select(t => t.Id));
        var visited = new HashSet<string>();
        var inPath = new HashSet<string>();
        string? captured = null;

        bool Dfs(string node, List<string> path)
        {
            if (inPath.Contains(node))
            {
                var cycleStart = path.IndexOf(node);
                var cycle = path.Skip(cycleStart).Concat(new[] { node });
                captured = $"cycle: {string.Join(" → ", cycle)}";
                return true;
            }

            if (visited.Contains(node))
                return false;

            visited.Add(node);
            inPath.Add(node);
            path.Add(node);

            if (adjacency.TryGetValue(node, out var deps))
            {
                foreach (var dep in deps)
                {
                    // dep may reference a task outside this batch (already in queue) — skip if unknown
                    if (!allIds.Contains(dep) && !adjacency.ContainsKey(dep))
                        continue;
                    if (Dfs(dep, path))
                        return true;
                }
            }

            path.RemoveAt(path.Count - 1);
            inPath.Remove(node);
            return false;
        }

        foreach (var t in tasks)
        {
            if (!visited.Contains(t.Id))
            {
                if (Dfs(t.Id, new List<string>()))
                {
                    cycleInfo = captured ?? "unknown cycle";
                    return true;
                }
            }
        }

        cycleInfo = "";
        return false;
    }

    public bool TryDequeue(out string taskId, out CoordinatorTask task)
    {
        if (_ready.TryTake(out taskId!))
        {
            lock (_lock)
            {
                if (_tasks.TryGetValue(taskId, out var t))
                {
                    t.Status = CoordinatorTaskStatus.Running;
                    task = t;
                    return true;
                }
            }
        }

        taskId = "";
        task = null!;
        return false;
    }

    public bool TryDequeueMany(Span<string> buffer, out int count)
    {
        count = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            if (!_ready.TryTake(out var id, 0))
                break;
            buffer[i] = id;
            count++;
        }

        if (count > 0)
        {
            lock (_lock)
            {
                for (int i = 0; i < count; i++)
                {
                    if (_tasks.TryGetValue(buffer[i], out var t))
                        t.Status = CoordinatorTaskStatus.Running;
                }
            }
        }

        return count > 0;
    }

    public CoordinatorTask? Get(string taskId)
    {
        lock (_lock)
        {
            _tasks.TryGetValue(taskId, out var t);
            return t;
        }
    }

    public IReadOnlyList<CoordinatorTask> GetAll()
    {
        lock (_lock) return _tasks.Values.ToList();
    }

    public void Complete(string taskId, string result)
    {
        List<string>? unblocked = null;

        lock (_lock)
        {
            if (!_tasks.TryGetValue(taskId, out var t)) return;
            t.Status = CoordinatorTaskStatus.Completed;
            t.Result = result;
            Interlocked.Increment(ref _completedCount);

            if (_dependents.TryGetValue(taskId, out var deps))
            {
                unblocked = new List<string>();
                foreach (var depId in deps)
                {
                    if (_pendingCounts.TryGetValue(depId, out var pc))
                    {
                        var remaining = pc - 1;
                        _pendingCounts[depId] = remaining;
                        if (remaining <= 0 && _tasks.TryGetValue(depId, out var depTask)
                            && depTask.Status == CoordinatorTaskStatus.Pending)
                        {
                            depTask.Status = CoordinatorTaskStatus.Ready;
                            unblocked.Add(depId);
                        }
                    }
                }
            }
        }

        if (unblocked != null)
        {
            foreach (var id in unblocked)
                _ready.Add(id);
        }
    }

    public void Fail(string taskId, string error)
    {
        List<(string Id, string Error)>? cascadeTargets = null;

        lock (_lock)
        {
            if (!_tasks.TryGetValue(taskId, out var t)) return;
            t.Error = error;

            if (t.Attempt < t.MaxRetries)
            {
                t.Attempt++;
                t.Status = CoordinatorTaskStatus.Ready;

                lock (_lock) { } // release before adding to ready queue
                _ready.Add(taskId);
                return;
            }

            t.Status = CoordinatorTaskStatus.Failed;
            Interlocked.Increment(ref _failedCount);

            cascadeTargets = new List<(string, string)>();
            CollectDependents(taskId, cascadeTargets, error);
        }

        if (cascadeTargets != null)
        {
            foreach (var (id, err) in cascadeTargets)
            {
                lock (_lock)
                {
                    if (_tasks.TryGetValue(id, out var cascadeTask)
                        && cascadeTask.Status is CoordinatorTaskStatus.Pending or CoordinatorTaskStatus.Ready)
                    {
                        cascadeTask.Status = CoordinatorTaskStatus.Failed;
                        cascadeTask.Error = err;
                        Interlocked.Increment(ref _failedCount);
                    }
                }
            }
        }
    }

    private void CollectDependents(string taskId, List<(string, string)> targets, string upstreamError)
    {
        if (!_dependents.TryGetValue(taskId, out var deps)) return;

        var cascadeError = $"上游任务失败 [{taskId}]: {upstreamError}";
        foreach (var depId in deps)
        {
            targets.Add((depId, cascadeError));
            CollectDependents(depId, targets, cascadeError);
        }
    }

    /// <summary>
    /// Detect stuck tasks whose dependencies can never be satisfied (deadlock).
    /// Returns IDs of tasks that are still Pending but their upstream tasks are all Failed/Completed
    /// yet they never transitioned — indicating a dependency on a task that was never enqueued.
    /// </summary>
    public HashSet<string> DetectDeadlocks()
    {
        lock (_lock)
        {
            var deadlocked = new HashSet<string>();
            var allIds = new HashSet<string>(_tasks.Keys);

            foreach (var (id, task) in _tasks)
            {
                if (task.Status != CoordinatorTaskStatus.Pending)
                    continue;

                // Check if ALL dependencies are resolved (completed or failed)
                // but the task is still pending — means a dep ID points to something not in this set
                if (task.DependsOn.Count > 0 && task.DependsOn.All(d =>
                    {
                        if (!_tasks.TryGetValue(d, out var depTask))
                            return true; // dependency never existed — orphaned
                        return depTask.Status is CoordinatorTaskStatus.Completed or CoordinatorTaskStatus.Failed;
                    }))
                {
                    deadlocked.Add(id);
                }
            }

            return deadlocked;
        }
    }

    /// <summary>
    /// Force-resolve deadlocked tasks by failing them.
    /// </summary>
    public int ResolveDeadlocks()
    {
        var deadlocked = DetectDeadlocks();
        foreach (var id in deadlocked)
        {
            if (_tasks.TryGetValue(id, out var t))
            {
                Fail(id, $"Deadlock detected: dependencies resolved but task never transitioned");
            }
        }
        return deadlocked.Count;
    }
}
