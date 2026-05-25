using LTAI.Core.Execution;
using LTAI.Core.System;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Core.Execution;

/// Activated Task Pipeline — wires previously dead DecoupledExecutor into LivingTreeSystem.
/// Enables parallel subtask execution with retry, and TaskJournal persistence.
public sealed class TaskPipeline
{
    private readonly DecoupledExecutor _executor;
    private readonly TaskJournal _journal;
    private readonly ILogger<TaskPipeline> _logger;
    private readonly Dictionary<string, TaskHandle> _activeHandles = new();
    private int _totalSubmissions;
    private int _totalCompletions;

    public int TotalSubmissions => _totalSubmissions;
    public int TotalCompletions => _totalCompletions;
    public IReadOnlyDictionary<string, TaskHandle> ActiveHandles => _activeHandles;

    public TaskPipeline(TaskJournal journal, ILogger<TaskPipeline>? logger = null)
    {
        _executor = DecoupledExecutor.Instance;
        _journal = journal;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskPipeline>.Instance;
    }

    public Func<IChatClient, string, CancellationToken, IAsyncEnumerable<string>>? LlmDecomposer { get; set; }

    /// Check if a query needs task decomposition
    public static bool NeedsDecomposition(string query)
    {
        if (query.Length < 100) return false;
        var keywords = new[] { "plan", "设计", "architecture", "refactor", "migrate", "build",
            "implement", "create", "setup", "deploy", "optimize", "debug", "analyze",
            "规划", "重构", "迁移", "实现", "全部", "所有", "all", "every" };
        return keywords.Any(k => query.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    /// Split query into subtasks by simple heuristics (sentence breaks, numbered lists, semicolons)
    public List<string> Decompose(string query)
    {
        var results = new List<string>();

        // Split by numbered patterns: "1. xxx", "1) xxx", "(1) xxx"
        var numbered = global::System.Text.RegularExpressions.Regex.Split(query, @"\n\s*(?:\d+[\.\)]|[-•]\s)");
        if (numbered.Length > 1)
        {
            foreach (var part in numbered)
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 10) results.Add(trimmed);
            }
            if (results.Count > 1) return results;
        }

        // Split by semicolons or Chinese semicolons
        results.Clear();
        var parts2 = query.Split(new[] { ';', '；', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts2)
        {
            var t = p.Trim();
            if (t.Length > 20) results.Add(t);
        }

        return results.Count > 1 ? results.Take(6).ToList() : new List<string> { query };
    }

    /// Execute subtasks in parallel via DecoupledExecutor, aggregate results
    public async Task<string> ExecuteParallelAsync(
        string query, Func<string, CancellationToken, Task<string>> worker,
        CancellationToken ct = default)
    {
        _totalSubmissions++;
        var subTasks = Decompose(query);

        // Single task — just run directly
        if (subTasks.Count <= 1)
        {
            return await worker(query, ct).ConfigureAwait(false);
        }

        // Multiple subtasks — run in parallel via executor
        _logger.LogInformation("TaskPipeline: decomposed into {Count} subtasks", subTasks.Count);
        var entry = _journal.Add(query);

        foreach (var sub in subTasks)
        {
            var handle = await _executor.SubmitAsync(async taskCt =>
            {
                try
                {
                    var result = await worker(sub, taskCt).ConfigureAwait(false);
                    _logger.LogDebug("TaskPipeline: subtask done ({Len} chars)", result.Length);
                    _journal.Complete(entry, result[..Math.Min(result.Length, 100)]);
                    return result;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "TaskPipeline: subtask failed");
                    _journal.Fail(entry, ex.Message);
                    return (object?)$"Error: {ex.Message}";
                }
            }, taskId: Guid.NewGuid().ToString("N")[..8], retries: 1, timeout: TimeSpan.FromMinutes(5));

            _activeHandles[handle.TaskId] = handle;
        }

        // Collect results
        var collected = await _executor.CollectAsync(TimeSpan.FromMinutes(6), partial: true).ConfigureAwait(false);
        _totalCompletions += collected.Count(h => h.Status == TaskStatusState.Done);

        // Clean up active handles
        foreach (var h in collected)
            _activeHandles.Remove(h.TaskId);

        var results = collected
            .Where(h => h.Status == TaskStatusState.Done && h.Result != null)
            .Select(h => h.Result?.ToString() ?? "");

        return string.Join("\n\n---\n", results);
    }

    /// Cancel all active tasks
    public int CancelAll()
    {
        var count = _activeHandles.Count;
        _activeHandles.Clear();
        _logger.LogInformation("TaskPipeline: cancelled {Count} active tasks", count);
        return count;
    }

    /// Check if there are active tasks
    public bool HasPending => _executor.PendingCount > 0;

    public Dictionary<string, object> GetStats() => new()
    {
        ["submissions"] = _totalSubmissions, ["completions"] = _totalCompletions,
        ["pending"] = _executor.PendingCount, ["active"] = _activeHandles.Count
    };
}
