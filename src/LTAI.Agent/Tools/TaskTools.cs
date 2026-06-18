using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using LTAI.AI;

namespace LTAI.Agent.Tools;

/// <summary>
/// In-session task/todo tracker.
/// Session-isolated: each session has its own todo list.
/// </summary>
[ToolDomain("task")]
public static class TaskTools
{
    /// <summary>Bounded todo store. Set during DI init; defaults to in-process static.</summary>
    public static TaskStore? Store { get; set; }

    // Legacy fallback when Store is not set
    private static readonly ConcurrentDictionary<string, SessionTodoList> _legacyTodos = new(StringComparer.Ordinal);
    private static readonly AsyncLocal<string?> _sessionId = new();
    private static DateTime _lastLegacyCleanup = DateTime.UtcNow;
    private static readonly TimeSpan LegacyCleanupInterval = TimeSpan.FromMinutes(15);

    /// <summary>Periodically evict stale session todo lists. Called by ChatAgent.</summary>
    public static void EvictStaleSessions()
    {
        if (Store != null)
        {
            Store.EvictStaleSessions();
            return;
        }
        var now = DateTime.UtcNow;
        if ((now - _lastLegacyCleanup) < LegacyCleanupInterval) return;
        _lastLegacyCleanup = now;
        foreach (var key in _legacyTodos.Keys.ToArray())
        {
            if (_legacyTodos.TryGetValue(key, out var list)
                && list.Items.Count == 0
                && (now - list.LastModified) > TimeSpan.FromHours(1))
            {
                _legacyTodos.TryRemove(key, out _);
            }
        }
    }

    /// <summary>Set by ChatAgent before tool calls, scoping todos to a session.</summary>
    public static string? SessionId { get => _sessionId.Value; set => _sessionId.Value = value; }

    private static SessionTodoList CurrentList
    {
        get
        {
            var sid = SessionId ?? "default";
            if (Store != null)
                return Store.GetOrAdd(sid, static () => new SessionTodoList());
            return _legacyTodos.GetOrAdd(sid, _ => new SessionTodoList());
        }
    }

    public sealed record TodoItem(
        int Id,
        string Content,
        string Status,
        string ActiveForm,
        DateTime CreatedAt);

    internal sealed class SessionTodoList
    {
        public readonly List<TodoItem> Items = new();
        public int NextId = 1;
        public DateTime LastModified = DateTime.UtcNow;
    }

    [Description("创建或更新待办事项列表。会替换所有现有的待办事项。\n"
        + "适用场景：列出需要完成的任务步骤、跟踪多步骤工作进度。\n"
        + "不适用场景：长期保存的任务（请用 MemoryTools）。\n"
        + "关键参数：todosJson — JSON 任务数组：[{content, status, activeForm}, ...]。")]
    [ToolExample("列出接下来要做的事")]
    [ToolExample("更新当前任务进度")]
    public static string TodoWrite(
        [Description("JSON array: [{content, status, activeForm}, ...]")] string todosJson)
    {
        try
        {
            var items = JsonSerializer.Deserialize<List<TodoInput>>(todosJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var list = CurrentList;
            lock (list)
            {
                list.LastModified = DateTime.UtcNow;
                if (items == null || items.Count == 0)
                {
                    list.Items.Clear();
                    return "Todo list cleared.";
                }

                list.Items.Clear();
                list.NextId = 1;

                foreach (var item in items)
                {
                    var status = item.Status?.ToLowerInvariant() switch
                    {
                        "completed" => "completed",
                        "in_progress" => "in_progress",
                        _ => "pending"
                    };
                    list.Items.Add(new TodoItem(list.NextId++, item.Content, status,
                        item.ActiveForm ?? item.Content, DateTime.UtcNow));
                }
            }

            return FormatTodos(list);
        }
        catch (JsonException ex)
        {
            return $"Invalid JSON: {ex.Message}";
        }
    }

    [Description("标记一个待办事项为已完成。\n"
        + "适用场景：完成一个步骤后标记进度、确认任务已完成。\n"
        + "关键参数：id — 待办事项 ID。")]
    [ToolExample("标记这个任务为已完成")]
    public static string TodoComplete(
        [Description("Todo item ID")] int id)
    {
        var list = CurrentList;
        lock (list)
        {
            list.LastModified = DateTime.UtcNow;
            var item = list.Items.FirstOrDefault(t => t.Id == id);
            if (item == null) return $"Todo #{id} not found";

            list.Items.Remove(item);
            list.Items.Add(item with { Status = "completed" });
            return $"✓ {item.Content}";
        }
    }

    [Description("查看当前待办事项列表。\n"
        + "适用场景：查看还有哪些任务没完成、检查当前工作进度。")]
    [ToolExample("看看还有哪些事要做")]
    public static string TodoList()
    {
        var list = CurrentList;
        lock (list)
        {
            if (list.Items.Count == 0) return "No todos.";
            return FormatTodos(list);
        }
    }

    /// <summary>Clear todo list for current session (e.g. on new request).</summary>
    public static void ClearCurrentSession()
    {
        var sid = SessionId ?? "default";
        if (Store != null)
            Store.Remove(sid);
        else
            _legacyTodos.TryRemove(sid, out _);
    }

    /// <summary>Get remaining todo count for current session (used by AgentModeObserver).</summary>
    public static (int remaining, int total) GetCounts()
    {
        var list = CurrentList;
        lock (list)
        {
            return (list.Items.Count(i => i.Status != "completed"), list.Items.Count);
        }
    }

    private static string FormatTodos(SessionTodoList list)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Todo List\n");
        sb.AppendLine("| # | Status | Task |");
        sb.AppendLine("|---|--------|------|");

        foreach (var t in list.Items)
        {
            var icon = t.Status switch
            {
                "completed" => "✅",
                "in_progress" => "🔄",
                _ => "⬜"
            };
            sb.AppendLine($"| {t.Id} | {icon} {t.Status} | {t.Content} |");
        }
        return sb.ToString();
    }

    private sealed record TodoInput(string Content, string? Status = null, string? ActiveForm = null);
}
