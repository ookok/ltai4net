using System.Text.Json;

namespace LTAI.TUI;

/// <summary>Parses tool result JSON from AI responses.</summary>
public static class ToolResultParser
{
    /// <summary>Try to parse a tool result JSON string. Returns (found, success, output, error).</summary>
    public static (bool found, bool success, string output, string error) Parse(string text)
    {
        if (TryParse(text, out var r))
            return (true, r.success, r.output, r.error);
        return (false, false, "", "");
    }

    /// <summary>Try to parse a tool result JSON string with out parameters.</summary>
    public static bool TryParse(string text, out (bool success, string output, string error) result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (!text.StartsWith('{') || !text.EndsWith('}')) return false;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("success", out var s)) return false;
            var ok = s.GetBoolean();
            result = (ok,
                ok && root.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "",
                !ok && root.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "");
            return true;
        }
        catch { return false; }
    }
}
