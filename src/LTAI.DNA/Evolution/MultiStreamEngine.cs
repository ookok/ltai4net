using LTAI.DNA.Models;

namespace LTAI.DNA.Evolution;

public sealed class MultiStreamEngine
{
    private readonly List<InputStream> _streamHistory = new();
    private readonly List<RunningTask> _taskHistory = new();
    private readonly object _lock = new();
    private string _activeTaskId = "";

    public Dictionary<string, object> Ingest(string content, StreamType type, StreamPriority priority,
        string? parentTaskId = null)
    {
        var stream = new InputStream
        {
            Type = type,
            Content = content,
            Priority = priority,
            ParentTaskId = parentTaskId
        };

        lock (_lock) { _streamHistory.Add(stream); }

        var interaction = ClassifyInteraction(stream);
        return interaction switch
        {
            "modify_running_task" => MergeIntoPlan(stream),
            "preempt_and_handle" => HandlePreempt(stream),
            "queue" => QueueStream(stream),
            _ => StartNewTask(stream)
        };
    }

    private string ClassifyInteraction(InputStream stream)
    {
        if (stream.Type == StreamType.Correction) return "modify_running_task";
        if (stream.Priority == StreamPriority.Critical) return "preempt_and_handle";
        if (!string.IsNullOrEmpty(stream.ParentTaskId)) return "modify_running_task";

        var taskKeywords = new[]
        {
            "continue", "next", "also", "and", "then", "additionally",
            "继续", "然后", "接下来", "还有", "另外"
        };
        foreach (var kw in taskKeywords)
            if (stream.Content.Contains(kw, StringComparison.OrdinalIgnoreCase)) return "modify_running_task";

        return "start_new";
    }

    private Dictionary<string, object> MergeIntoPlan(InputStream stream)
    {
        RunningTask? activeTask;
        lock (_lock)
        {
            activeTask = _taskHistory.Find(t =>
                t.TaskId == (stream.ParentTaskId ?? _activeTaskId) && t.Status == "running");
        }

        if (activeTask == null) return StartNewTask(stream);

        var modification = ExtractModification(stream);
        var changeType = modification.GetValueOrDefault("type", "add_step") as string ?? "add_step";

        lock (_lock)
        {
            activeTask.LastModified = DateTime.UtcNow;
            activeTask.Modifications.Add(stream.Content);

            switch (changeType)
            {
                case "add_step":
                    activeTask.Plan.Add(modification.GetValueOrDefault("step", "") as string ?? stream.Content);
                    break;
                case "modify_step":
                    var idx = (int)modification.GetValueOrDefault("index", -1);
                    if (idx >= 0 && idx < activeTask.Plan.Count)
                        activeTask.Plan[idx] = modification.GetValueOrDefault("step", "") as string ?? stream.Content;
                    break;
                case "remove_step":
                    var rIdx = (int)modification.GetValueOrDefault("index", -1);
                    if (rIdx >= 0 && rIdx < activeTask.Plan.Count)
                        activeTask.Plan.RemoveAt(rIdx);
                    break;
                case "change_direction":
                    activeTask.Plan.Insert(0,
                        $"REDIRECT: {modification.GetValueOrDefault("step", "") ?? stream.Content}");
                    break;
            }
        }

        return new Dictionary<string, object>
        {
            ["action"] = "merged",
            ["task_id"] = activeTask.TaskId,
            ["change_type"] = changeType,
            ["plan_after"] = activeTask.Plan
        };
    }

    private Dictionary<string, object> ExtractModification(InputStream stream)
    {
        var content = stream.Content.ToLowerInvariant();
        if (content.Contains("add step") || content.Contains("新增步骤") || content.Contains("添加"))
            return new Dictionary<string, object> { ["type"] = "add_step", ["step"] = stream.Content };
        if (content.Contains("remove step") || content.Contains("删除步骤") || content.Contains("移除"))
            return new Dictionary<string, object> { ["type"] = "remove_step", ["index"] = 0 };
        if (content.Contains("change step") || content.Contains("修改步骤") || content.Contains("改为"))
            return new Dictionary<string, object> { ["type"] = "modify_step", ["index"] = 0, ["step"] = stream.Content };
        if (content.Contains("change direction") || content.Contains("新方向") || content.Contains("换一个"))
            return new Dictionary<string, object> { ["type"] = "change_direction", ["step"] = stream.Content };

        return new Dictionary<string, object> { ["type"] = "add_step", ["step"] = stream.Content };
    }

    private Dictionary<string, object> HandlePreempt(InputStream stream)
    {
        lock (_lock)
        {
            foreach (var task in _taskHistory.Where(t => t.Status == "running"))
                task.Status = "paused";
        }

        var result = StartNewTask(stream);
        result["preempted"] = true;
        return result;
    }

    private Dictionary<string, object> QueueStream(InputStream stream)
    {
        lock (_lock) { stream.Processed = false; }
        return new Dictionary<string, object> { ["action"] = "queued", ["stream_id"] = stream.StreamId };
    }

    private Dictionary<string, object> StartNewTask(InputStream stream)
    {
        var task = new RunningTask
        {
            Description = stream.Content,
            Status = "running",
            Plan = new List<string> { $"Process: {stream.Content}" }
        };

        lock (_lock)
        {
            _taskHistory.Add(task);
            _activeTaskId = task.TaskId;
            stream.Processed = true;
        }

        return new Dictionary<string, object>
        {
            ["action"] = "started",
            ["task_id"] = task.TaskId,
            ["plan"] = task.Plan
        };
    }

    public Dictionary<string, object> AllocateAttention()
    {
        lock (_lock)
        {
            var unprocessedCritical = _streamHistory
                .Where(s => !s.Processed && s.Priority == StreamPriority.Critical).ToList();
            if (unprocessedCritical.Count > 0)
                return new() { ["action"] = "process_critical", ["streams"] = unprocessedCritical.Select(s => s.StreamId).ToList() };

            var runningTask = _taskHistory.Find(t => t.Status == "running");
            if (runningTask != null)
                return new() { ["action"] = "continue_task", ["task_id"] = runningTask.TaskId, ["completed"] = runningTask.CompletedSteps, ["total"] = runningTask.Plan.Count };

            var nextQueued = _streamHistory
                .Where(s => !s.Processed).OrderBy(s => (int)s.Priority).FirstOrDefault();
            if (nextQueued != null)
                return new() { ["action"] = "start_queued", ["stream_id"] = nextQueued.StreamId };

            return new() { ["action"] = "idle" };
        }
    }

    public List<Dictionary<string, object>> DetectConflicts()
    {
        var conflicts = new List<Dictionary<string, object>>();
        List<RunningTask> tasks;
        lock (_lock) { tasks = _taskHistory.ToList(); }

        foreach (var t1 in tasks)
        foreach (var t2 in tasks)
        {
            if (t1.TaskId == t2.TaskId) continue;
            var overlap = t1.Plan.Intersect(t2.Plan).Count();
            if (overlap > 0)
                conflicts.Add(new()
                {
                    ["task_a"] = t1.TaskId,
                    ["task_b"] = t2.TaskId,
                    ["overlap_steps"] = overlap,
                    ["type"] = "plan_overlap"
                });

            var addSteps = t1.Modifications
                .Concat(t2.Modifications)
                .Where(m => m.Contains("add", StringComparison.OrdinalIgnoreCase)).Count();
            var removeSteps = t1.Modifications
                .Concat(t2.Modifications)
                .Where(m => m.Contains("remove", StringComparison.OrdinalIgnoreCase)).Count();
            if (addSteps > 0 && removeSteps > 0)
                conflicts.Add(new()
                {
                    ["task_a"] = t1.TaskId,
                    ["task_b"] = t2.TaskId,
                    ["type"] = "add_remove_conflict"
                });
        }

        return conflicts;
    }

    public Dictionary<string, object> BatchIngest(List<(string content, StreamType type, StreamPriority priority)> items)
    {
        var results = new List<Dictionary<string, object>>();
        foreach (var (content, type, priority) in items)
            results.Add(Ingest(content, type, priority));

        var conflicts = DetectConflicts();

        return new Dictionary<string, object>
        {
            ["results"] = results,
            ["conflicts"] = conflicts,
            ["conflict_count"] = conflicts.Count
        };
    }

    public Dictionary<string, object> Stats()
    {
        lock (_lock)
        {
            return new Dictionary<string, object>
            {
                ["total_streams"] = _streamHistory.Count,
                ["total_tasks"] = _taskHistory.Count,
                ["active_task_id"] = _activeTaskId,
                ["running_tasks"] = _taskHistory.Count(t => t.Status == "running"),
                ["completed_tasks"] = _taskHistory.Count(t => t.Status == "completed"),
                ["paused_tasks"] = _taskHistory.Count(t => t.Status == "paused")
            };
        }
    }
}
