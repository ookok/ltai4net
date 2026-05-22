using System.Collections.Concurrent;

namespace LTAI.Knowledge.Core;

public sealed record AgentTaskContext(
    string TaskId,
    string TaskDescription,
    DateTime StartedAt,
    List<string> SearchedSections,
    List<string> ModifiedFiles,
    Dictionary<string, string> Decisions)
{
    public string AgentState => ModifiedFiles.Count > 0 ? "modified" : "exploring";
}

public sealed record AgentSessionAction(
    string ActionType,
    string Detail,
    DateTime Timestamp,
    Dictionary<string, string>? Metadata)
{
    public string ActionId => $"{ActionType}_{Timestamp:yyyyMMddHHmmss}";
}

public enum AgentWorkflowPhase
{
    BeforeTask,
    DuringTask,
    AfterTask,
    Error,
    Checkpoint
}

public sealed class LatAgentHook
{
    private readonly MarkdownKnowledgeGraph _kg;
    private readonly LatCheckValidator _validator;
    private readonly CodeLinkTracker? _tracker;
    private readonly TestSpecEnforcer? _enforcer;
    private readonly ConcurrentDictionary<string, AgentTaskContext> _activeTasks = new();
    private readonly ConcurrentQueue<AgentSessionAction> _actionLog = new();
    private readonly string _sourceRoot;

    public event Action<AgentWorkflowPhase, AgentTaskContext>? OnPhaseChange;
    public event Action<string, LatCheckResult>? OnCheckComplete;

    public LatAgentHook(
        MarkdownKnowledgeGraph kg,
        LatCheckValidator validator,
        string sourceRoot,
        CodeLinkTracker? tracker = null,
        TestSpecEnforcer? enforcer = null)
    {
        _kg = kg;
        _validator = validator;
        _sourceRoot = sourceRoot;
        _tracker = tracker;
        _enforcer = enforcer;
    }

    public async Task<AgentTaskContext> BeforeTaskAsync(string taskDescription)
    {
        var taskId = $"task_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{Guid.NewGuid():N}";

        var context = new AgentTaskContext(
            TaskId: taskId,
            TaskDescription: taskDescription,
            StartedAt: DateTime.UtcNow,
            SearchedSections: new(),
            ModifiedFiles: new(),
            Decisions: new());

        _activeTasks[taskId] = context;

        LogAction("before_task", $"Starting: {taskDescription}");

        var results = await _kg.SearchAsync(taskDescription, topK: 5);
        foreach (var (section, score) in results)
        {
            context.SearchedSections.Add(section.FullId);
        }

        if (_tracker != null)
        {
            var codeRefs = _tracker.FindSectionCodeRefs(taskDescription);
            foreach (var ref_ in codeRefs.Take(5))
            {
                context.SearchedSections.Add(ref_.SourceFilePath);
            }
        }

        OnPhaseChange?.Invoke(AgentWorkflowPhase.BeforeTask, context);
        return context;
    }

    public void DuringTask(AgentTaskContext context, string action, Dictionary<string, string>? metadata = null)
    {
        LogAction("during_task", $"{context.TaskId}: {action}", metadata);
    }

    public async Task<LatCheckResult> AfterTaskAsync(AgentTaskContext context)
    {
        var taskId = context.TaskId;

        foreach (var file in context.ModifiedFiles)
        {
            var fullPath = Path.Combine(_sourceRoot, file);
            if (File.Exists(fullPath) && file.EndsWith(".cs"))
            {
                _tracker?.ScanFile(fullPath);
            }

            if (file.StartsWith("lat.md/") && File.Exists(Path.Combine(_sourceRoot, file)))
            {
                var content = File.ReadAllText(Path.Combine(_sourceRoot, file));
                _kg.AddOrUpdateFile(file, content);
            }
        }

        var result = _validator.ValidateAll(includeCodeScan: true);

        LogAction("after_task", $"Completed: {context.TaskDescription} - Valid: {result.AllValid}");

        if (!result.AllValid)
        {
            foreach (var err in result.Errors.Take(5))
            {
                LogAction("check_error", $"{err.SourceSection}: {err.Message}");
            }
        }

        OnPhaseChange?.Invoke(AgentWorkflowPhase.AfterTask, context);
        OnCheckComplete?.Invoke(taskId, result);

        _activeTasks.TryRemove(taskId, out _);

        return result;
    }

    public async Task<LatCheckResult> RunCheckAsync(string taskId = "manual")
    {
        var result = _validator.ValidateAll(includeCodeScan: true);
        OnCheckComplete?.Invoke(taskId, result);

        if (!result.AllValid)
        {
            foreach (var err in result.Errors.Take(5))
            {
                LogAction("check_error", $"{err.SourceSection}: {err.Message}");
            }
        }

        return result;
    }

    public LatCheckResult QuickCheck()
    {
        return _validator.ValidateAll(includeCodeScan: false);
    }

    public bool IsKnowledgeGraphHealthy()
    {
        var result = _validator.ValidateAll(includeCodeScan: false);
        return result.AllValid;
    }

    public List<AgentSessionAction> GetRecentActions(int count = 20)
    {
        return _actionLog.ToList().TakeLast(count).ToList();
    }

    public List<AgentTaskContext> GetActiveTasks()
    {
        return _activeTasks.Values.ToList();
    }

    private void LogAction(string type, string detail, Dictionary<string, string>? metadata = null)
    {
        _actionLog.Enqueue(new AgentSessionAction(type, detail, DateTime.UtcNow, metadata));
        while (_actionLog.Count > 500)
            _actionLog.TryDequeue(out _);
    }
}
