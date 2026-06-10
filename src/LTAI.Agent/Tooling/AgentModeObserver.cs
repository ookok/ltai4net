using System.Text.Json.Serialization;

namespace LTAI.Agent.Tooling;

/// <summary>
/// Lightweight observer for agent mode state, consumed by frontends (TUI/Desktop).
/// ChatAgent updates this on each turn so the UI can display current mode without
/// direct access to AgentSession.StateBag.
/// </summary>
public static class AgentModeObserver
{
    /// <summary>Current agent operating mode: plan / execute / chat (or custom).</summary>
    public static string CurrentMode { get; set; } = "chat";

    /// <summary>Incomplete todo count (updated by ChatAgent after each turn).</summary>
    public static int RemainingTodos { get; set; }

    /// <summary>Full todo summary text for display (set by ChatAgent).</summary>
    public static string? TodoSummary { get; set; }

    /// <summary>Total todos across all states.</summary>
    public static int TotalTodos { get; set; }

    public static string ModeIcon => CurrentMode.ToLowerInvariant() switch
    {
        "plan" => "🅿",
        "execute" or "exec" => "⚡",
        "chat" => "💬",
        _ => "🔘",
    };
}

// DTOs for reading MAF provider state from AgentSession.StateBag.
// MAF's internal state types (AgentModeState, TodoState, TodoItem) are internal,
// so we define matching public types with identical JsonPropertyName attributes.

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
