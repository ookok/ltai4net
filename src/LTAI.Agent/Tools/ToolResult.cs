using System.Text.Json;

namespace LTAI.Agent.Tools;

/// <summary>
/// Unified tool return format — structured JSON, not plain text.
/// Ported from DeepSeek-Reasonix formatSubagentResult pattern.
/// </summary>
public static class ToolResult
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    public static string Success(string output, object? extra = null)
    {
        var result = new Dictionary<string, object?>
        {
            ["success"] = true,
            ["output"] = output,
        };
        if (extra != null) result["extra"] = extra;
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    public static string Error(string error, object? extra = null)
    {
        var result = new Dictionary<string, object?>
        {
            ["success"] = false,
            ["error"] = error,
        };
        if (extra != null) result["extra"] = extra;
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    public static string FromException(Exception ex, string? context = null)
    {
        var msg = context != null ? $"{context}: {ex.Message}" : ex.Message;
        return Error(msg, new { type = ex.GetType().Name });
    }

    /// <summary>Plain-text coreutils-style error: `tool: message`</summary>
    public static string ErrorText(string tool, string message)
        => $"{tool}: {message}";

    /// <summary>Plain-text coreutils-style ok: just the output</summary>
    public static string OkText(string output) => output;
}
