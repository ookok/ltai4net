using LTAI.AI;

namespace LTAI.TUI.Services;

public static class CommandHelpers
{
    public static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
        if (bytes >= 1024L) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }

    public static string FormatNum(int n) =>
        n >= 1_000_000 ? $"[grey]{n / 1_000_000}M ctx[/]" :
        n >= 1_000    ? $"[grey]{n / 1_000}K ctx[/]" :
        $"[grey]{n}[/]";

    public static string AbbrevCaps(ModelCapability caps)
    {
        if (caps == 0) return "";
        var parts = new List<string>();
        if (caps.HasFlag(ModelCapability.Chat)) parts.Add("Chat");
        if (caps.HasFlag(ModelCapability.Streaming)) parts.Add("Str");
        if (caps.HasFlag(ModelCapability.ToolCall)) parts.Add("Tool");
        if (caps.HasFlag(ModelCapability.FunctionCall)) parts.Add("Func");
        if (caps.HasFlag(ModelCapability.StructuredOutput)) parts.Add("Struct");
        if (caps.HasFlag(ModelCapability.Vision)) parts.Add("Vis");
        if (caps.HasFlag(ModelCapability.Embedding)) parts.Add("Emb");
        if (caps.HasFlag(ModelCapability.ImageGeneration)) parts.Add("Img");
        return string.Join(", ", parts);
    }
}
