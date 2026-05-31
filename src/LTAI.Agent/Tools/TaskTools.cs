using System.ComponentModel;
using System.Text;
using System.Text.Json;
using LTAI.AI;

namespace LTAI.Agent.Tools;

/// <summary>
/// In-session task/todo tracker.
/// </summary>
[ToolDomain("task")]
public static class TaskTools
{
    private static readonly List<TodoItem> _todos = new();
    private static int _nextId = 1;

    public sealed record TodoItem(
        int Id,
        string Content,
        string Status,
        string ActiveForm,
        DateTime CreatedAt);

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

            if (items == null || items.Count == 0)
            {
                _todos.Clear();
                return "Todo list cleared.";
            }

            _todos.Clear();
            _nextId = 1;

            foreach (var item in items)
            {
                var status = item.Status?.ToLowerInvariant() switch
                {
                    "completed" => "completed",
                    "in_progress" => "in_progress",
                    _ => "pending"
                };
                _todos.Add(new TodoItem(_nextId++, item.Content, status,
                    item.ActiveForm ?? item.Content, DateTime.UtcNow));
            }

            return FormatTodos();
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
        var item = _todos.FirstOrDefault(t => t.Id == id);
        if (item == null) return $"Todo #{id} not found";

        _todos.Remove(item);
        _todos.Add(item with { Status = "completed" });
        return $"✓ {item.Content}";
    }

    [Description("查看当前待办事项列表。\n"
        + "适用场景：查看还有哪些任务没完成、检查当前工作进度。")]
    [ToolExample("看看还有哪些事要做")]
    public static string TodoList()
    {
        if (_todos.Count == 0) return "No todos.";
        return FormatTodos();
    }

    private static string FormatTodos()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Todo List\n");
        sb.AppendLine("| # | Status | Task |");
        sb.AppendLine("|---|--------|------|");

        foreach (var t in _todos)
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
