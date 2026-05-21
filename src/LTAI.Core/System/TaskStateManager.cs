using System.Text.Json;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Options;

namespace LTAI.Core.System;

public sealed class TaskProgress
{
    public string TaskId { get; set; } = "";
    public string TaskName { get; set; } = "";
    public int TotalItems { get; set; }
    public int CompletedItems { get; set; }
    public int FailedItems { get; set; }
    public string CurrentItem { get; set; } = "";
    public int CurrentIndex { get; set; }
    public double Percent => TotalItems > 0 ? (double)CompletedItems / TotalItems * 100 : 0;
    public string StartedAt { get; set; } = "";
    public string LastCheckpoint { get; set; } = "";
    public List<string> Warnings { get; set; } = new();
}

public sealed class PromptInjector
{
    private static readonly Lazy<PromptInjector> _instance = new(() => new PromptInjector());
    public static PromptInjector Instance => _instance.Value;

    public const string InvestigateBeforeAnswering = """
        Never speculate about content you haven't read. If referencing specific files or data,
        you must read them first before answering. Before proposing solutions, always check
        relevant files and data. When answering knowledge questions, cite sources you actually
        retrieved, don't fabricate. Ensure answers are pragmatic, accurate, and hallucination-free.
        """;

    public const string ProgressiveWorkPattern = """
        This is a long task. Progress incrementally, focusing on a few items at a time.
        Track your progress. Don't exhaust context with large uncommitted work.
        Systematically continue until the task is complete. Before context window approaches
        limits, save current progress and state to memory/file.
        """;

    public const string ParallelExecution = """
        If you plan to call multiple tools with no inter-dependencies, make all independent
        tool calls in parallel. Prioritize simultaneous calls when operations can be parallel
        rather than sequential. For example: read multiple files at once, make multiple
        search queries at once. Maximize parallelism for speed and efficiency.
        """;

    public const string AntiOverengineering = """
        Only make changes that are directly requested or clearly necessary. Keep solutions
        simple and focused. Don't add features, refactor code, or make "improvements" beyond
        requirements. Don't design for hypothetical future needs. The right complexity is
        the minimum needed for the current task.
        """;

    public const string SourceVerification = """
        When gathering data, verify information across multiple sources. Develop competing
        hypotheses. Track confidence levels. Periodically self-critique methods and plans.
        Persist findings to files for transparency.
        """;

    public static readonly string GatewaySystemPrompt =
        $"{InvestigateBeforeAnswering}\n{ProgressiveWorkPattern}\n{SourceVerification}\n{AntiOverengineering}";

    private readonly string _investigate;
    private readonly string _progressive;
    private readonly string _parallel;
    private readonly string _antiOverengineering;
    private readonly string _sourceVerification;

    private readonly Dictionary<string, string> _modes;

    public PromptInjector()
    {
        _investigate = InvestigateBeforeAnswering;
        _progressive = ProgressiveWorkPattern;
        _parallel = ParallelExecution;
        _antiOverengineering = AntiOverengineering;
        _sourceVerification = SourceVerification;
        _modes = BuildModes();
    }

    public PromptInjector(IOptions<LTAIOptions> options)
    {
        var sp = options.Value.AI.SystemPrompts;
        _investigate = sp.InvestigateBeforeAnswering;
        _progressive = sp.ProgressiveWorkPattern;
        _parallel = sp.ParallelExecution;
        _antiOverengineering = sp.AntiOverengineering;
        _sourceVerification = sp.SourceVerification;
        _modes = BuildModes();
    }

    private Dictionary<string, string> BuildModes()
    {
        var gateway = $"{_investigate}\n{_progressive}\n{_sourceVerification}\n{_antiOverengineering}";
        return new()
        {
            ["gateway"] = gateway,
            ["research"] = $"{_investigate}\n{_sourceVerification}",
            ["coding"] = $"{_progressive}\n{_parallel}\n{_antiOverengineering}",
            ["light"] = _investigate
        };
    }

    public List<Dictionary<string, string>> Inject(List<Dictionary<string, string>> messages, string mode = "gateway")
    {
        // Integration point: Call MemPOOptimizer.Models.BuildContext() before injection
        // to include optimized memory context in the system prompt.
        var prompt = _modes.GetValueOrDefault(mode, _modes["gateway"]);

        if (messages.Count > 0 && messages[0].TryGetValue("role", out var role) && role == "system")
        {
            messages[0]["content"] = prompt + "\n\n" + messages[0]["content"];
        }
        else
        {
            messages.Insert(0, new Dictionary<string, string> { ["role"] = "system", ["content"] = prompt });
        }

        return messages;
    }

    public List<Dictionary<string, string>> InjectForTask(List<Dictionary<string, string>> messages, string taskType)
    {
        var taskModes = new Dictionary<string, string>
        {
            ["search"] = "research",
            ["analyze"] = "research",
            ["generate"] = "coding",
            ["code"] = "coding",
            ["chat"] = "light"
        };

        var mode = taskModes.GetValueOrDefault(taskType, "gateway");
        return Inject(messages, mode);
    }
}

public sealed class TaskStateManager
{
    private static readonly Lazy<TaskStateManager> _instance = new(() => new TaskStateManager());
    public static TaskStateManager Instance => _instance.Value;

    private readonly string _dataDir;
    private TaskProgress? _activeTask;
    private readonly object _lock = new();

    private TaskStateManager(string? dataDir = null)
    {
        _dataDir = dataDir ?? global::System.IO.Path.Combine(".livingtree", "tasks");
        global::System.IO.Directory.CreateDirectory(_dataDir);
    }

    public TaskStateManager(DataPathResolver dataPath)
    {
        _dataDir = dataPath.GetPath("tasks");
        global::System.IO.Directory.CreateDirectory(_dataDir);
    }

    public TaskProgress StartTask(string taskId, string taskName, int totalItems = 0)
    {
        var task = new TaskProgress
        {
            TaskId = taskId,
            TaskName = taskName,
            TotalItems = totalItems,
            StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            LastCheckpoint = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
        };

        lock (_lock) { _activeTask = task; }
        Save(task);
        WriteProgressNote(taskId, "START", task.StartedAt, $"Task started: {taskName} ({totalItems} items)");
        return task;
    }

    public TaskProgress? UpdateProgress(string? taskId = null, int completed = -1, int failed = -1,
        string current = "", string? itemStatus = null)
    {
        var task = _activeTask ?? (taskId != null ? Load(taskId) : null);
        if (task == null) return null;

        if (completed >= 0) task.CompletedItems = completed;
        if (failed >= 0) task.FailedItems = failed;
        if (current.Length > 0) task.CurrentItem = current;

        task.LastCheckpoint = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        Save(task);

        if (task.CompletedItems % 10 == 0 || task.Percent >= 100)
        {
            WriteProgressNote(task.TaskId, "PROGRESS", task.LastCheckpoint,
                $"Progress: {task.CompletedItems}/{task.TotalItems} ({task.Percent:F0}%)");
        }

        return task;
    }

    public TaskProgress? Checkpoint(string? taskId = null, string reason = "")
    {
        var task = _activeTask ?? (taskId != null ? Load(taskId) : null);
        if (task == null) return null;

        task.LastCheckpoint = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        Save(task);

        WriteProgressNote(task.TaskId, "CHECKPOINT", task.LastCheckpoint,
            $"CHECKPOINT: {reason} — {task.CompletedItems}/{task.TotalItems} done");
        return task;
    }

    public TaskProgress? DiscoverAndResume(string taskPrefix = "")
    {
        var taskFiles = new List<(string path, DateTime mtime)>();
        try
        {
            foreach (var f in global::System.IO.Directory.GetFiles(_dataDir, "task_*.json"))
                taskFiles.Add((f, global::System.IO.File.GetLastWriteTimeUtc(f)));
        }
        catch { /* non-fatal */ }

        if (taskFiles.Count == 0) return null;
        taskFiles.Sort((a, b) => b.mtime.CompareTo(a.mtime));

        foreach (var (path, _) in taskFiles)
        {
            var task = LoadFromPath(path);
            if (task != null && task.TaskId.Contains(taskPrefix) && task.Percent < 100)
            {
                lock (_lock) { _activeTask = task; }
                return task;
            }
        }

        return null;
    }

    public bool IsComplete(string? taskId = null)
    {
        var task = _activeTask ?? (taskId != null ? Load(taskId) : null);
        return task != null && task.Percent >= 100;
    }

    public List<Dictionary<string, object>> ListTasks()
    {
        var tasks = new List<Dictionary<string, object>>();
        try
        {
            foreach (var f in global::System.IO.Directory.GetFiles(_dataDir, "task_*.json"))
            {
                var task = LoadFromPath(f);
                if (task != null)
                {
                    tasks.Add(new Dictionary<string, object>
                    {
                        ["id"] = task.TaskId,
                        ["name"] = task.TaskName,
                        ["percent"] = Math.Round(task.Percent, 1),
                        ["completed"] = task.CompletedItems,
                        ["total"] = task.TotalItems,
                        ["active"] = task.Percent < 100
                    });
                }
            }
        }
        catch { /* non-fatal */ }
        return tasks.OrderByDescending(t => (double)(t["percent"] ?? 0)).ToList();
    }

    private void Save(TaskProgress task)
    {
        var path = global::System.IO.Path.Combine(_dataDir, $"task_{task.TaskId}.json");
        var data = new Dictionary<string, object>
        {
            ["task_id"] = task.TaskId,
            ["task_name"] = task.TaskName,
            ["total_items"] = task.TotalItems,
            ["completed_items"] = task.CompletedItems,
            ["failed_items"] = task.FailedItems,
            ["current_item"] = task.CurrentItem,
            ["current_index"] = task.CurrentIndex,
            ["percent"] = task.Percent,
            ["started_at"] = task.StartedAt,
            ["last_checkpoint"] = task.LastCheckpoint,
            ["warnings"] = task.Warnings
        };
        global::System.IO.File.WriteAllText(path, JsonSerializer.Serialize(data));
    }

    private TaskProgress? Load(string taskId)
    {
        var path = global::System.IO.Path.Combine(_dataDir, $"task_{taskId}.json");
        return LoadFromPath(path);
    }

    private static TaskProgress? LoadFromPath(string path)
    {
        if (!global::System.IO.File.Exists(path)) return null;
        try
        {
            var json = global::System.IO.File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (data == null) return null;

            return new TaskProgress
            {
                TaskId = data.TryGetValue("task_id", out var id) ? id.GetString() ?? "" : "",
                TaskName = data.TryGetValue("task_name", out var n) ? n.GetString() ?? "" : "",
                TotalItems = data.TryGetValue("total_items", out var ti) ? ti.GetInt32() : 0,
                CompletedItems = data.TryGetValue("completed_items", out var ci) ? ci.GetInt32() : 0,
                FailedItems = data.TryGetValue("failed_items", out var fi) ? fi.GetInt32() : 0,
                CurrentItem = data.TryGetValue("current_item", out var cur) ? cur.GetString() ?? "" : "",
                StartedAt = data.TryGetValue("started_at", out var sa) ? sa.GetString() ?? "" : "",
                LastCheckpoint = data.TryGetValue("last_checkpoint", out var lc) ? lc.GetString() ?? "" : ""
            };
        }
        catch { return null; }
    }

    private void WriteProgressNote(string taskId, string session, string timestamp, string summary)
    {
        var path = global::System.IO.Path.Combine(_dataDir, $"progress_{taskId}.md");
        var entry = $"## {session} — {timestamp}\n{summary}\n\n---\n\n";
        try
        {
            global::System.IO.File.AppendAllText(path, entry);
        }
        catch { /* non-fatal */ }
    }
}
