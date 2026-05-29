using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace LTAI.Agent.Tools;

/// <summary>
/// In-session task/todo tracker.
/// Ported from DeepSeek-Reasonix todo.ts + plan-core.ts pattern.
/// </summary>
public static class TaskTools
{
    private static readonly List<TodoItem> _todos = new();
    private static int _nextId = 1;

    public sealed record TodoItem(
        int Id,
        string Content,
        string Status,  // "pending" | "in_progress" | "completed"
        string ActiveForm,
        DateTime CreatedAt);

    [Description("Create or update the todo list. Replaces ALL existing todos.")]
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

    [Description("Mark a todo item as completed")]
    public static string TodoComplete(
        [Description("Todo item ID")] int id)
    {
        var item = _todos.FirstOrDefault(t => t.Id == id);
        if (item == null) return $"Todo #{id} not found";

        _todos.Remove(item);
        _todos.Add(item with { Status = "completed" });
        return $"✓ {item.Content}";
    }

    [Description("Show current todo list")]
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
