using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media;

namespace LTAI.Desktop.ToolRendering;

public sealed class ToolResultJsonRenderer : IToolResultRenderer
{
    public bool CanRender(string token) =>
        (token.StartsWith("{\"success\":") || token.StartsWith("{\"success\" :")) && token.Contains("\"output\"");

    public Control? Render(string token, string? context = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(token);
            var root = doc.RootElement;
            var success = root.TryGetProperty("success", out var s) && s.GetBoolean();
            var output = root.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";

            var color = success ? "#a371f7" : "#e74c3c";
            var label = success ? "✅" : "❌";
            var preview = output.Length > 80 ? output[..80] + "..." : output;

            return new TextBlock
            {
                Text = $"{label} {preview}",
                Foreground = LtaiTheme.Sbb(color),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            };
        }
        catch
        {
            return null;
        }
    }
}
