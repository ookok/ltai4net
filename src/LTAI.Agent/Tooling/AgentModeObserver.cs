using System.Text.Json.Serialization;

namespace LTAI.Agent.Tooling;

/// <summary>
/// Lightweight observer for agent mode state, consumed by frontends (TUI/Desktop).
/// DI singleton. Static properties delegate to <see cref="Default"/> for backward compat.
/// </summary>
public sealed class AgentModeObserver
{
    public static AgentModeObserver Default { get; set; } = new();

    public string CurrentModeInstance { get; set; } = "chat";
    public int RemainingTodosInstance { get; set; }
    public string? TodoSummaryInstance { get; set; }
    public int TotalTodosInstance { get; set; }

    public string ModeIconInstance => CurrentModeInstance.ToLowerInvariant() switch
    {
        "plan" => "🅿",
        "execute" or "exec" => "⚡",
        "chat" => "💬",
        _ => "🔘",
    };

    // Static forwarding (backward compat)
    public static string CurrentMode { get => Default.CurrentModeInstance; set => Default.CurrentModeInstance = value; }
    public static int RemainingTodos { get => Default.RemainingTodosInstance; set => Default.RemainingTodosInstance = value; }
    public static string? TodoSummary { get => Default.TodoSummaryInstance; set => Default.TodoSummaryInstance = value; }
    public static int TotalTodos { get => Default.TotalTodosInstance; set => Default.TotalTodosInstance = value; }
    public static string ModeIcon => Default.ModeIconInstance;
}

public sealed class ObservableAgentModeState
{
    [JsonPropertyName("currentMode")]
    public string? CurrentMode { get; set; }
}

public sealed class ObservableTodoState
{
    [JsonPropertyName("items")]
    public System.Collections.Generic.List<ObservableTodoItem>? Items { get; set; }
}

public sealed class ObservableTodoItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;
    [JsonPropertyName("description")]
    public string? Description { get; set; }
    [JsonPropertyName("isComplete")]
    public bool IsComplete { get; set; }
}
