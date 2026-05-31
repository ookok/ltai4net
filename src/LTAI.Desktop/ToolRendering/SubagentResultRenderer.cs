using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace LTAI.Desktop.ToolRendering;

public sealed class SubagentResultRenderer : IToolResultRenderer
{
    public bool CanRender(string token)
    {
        try
        {
            using var doc = JsonDocument.Parse(token);
            var root = doc.RootElement;
            return root.TryGetProperty("success", out var s) && s.GetBoolean()
                && root.TryGetProperty("type", out _)
                && root.TryGetProperty("messages", out _);
        }
        catch { return false; }
    }

    public Control? Render(string token, string? context = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(token);
            var root = doc.RootElement;
            var output = root.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
            var spawnCount = root.TryGetProperty("spawnCount", out var sc) ? sc.GetInt32() : 0;
            var elapsedMs = root.TryGetProperty("elapsedMs", out var em) ? em.GetInt64() : 0;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "generic" : "generic";

            var preview = output.Length > 60 ? output[..60] + "..." : output;

            var dock = new DockPanel { Margin = new(0, 2) };
            var icon = new TextBlock
            {
                Text = $"🔧 子任务 #{spawnCount} ({type})",
                Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
                FontSize = 11,
                FontFamily = new("Consolas")
            };
            dock.Children.Add(icon);

            var elapsedText = $"  [{elapsedMs / 1000}.{(elapsedMs % 1000) / 100}s]";
            var elapsedBlock = new TextBlock
            {
                Text = elapsedText,
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(elapsedBlock, Dock.Right);
            dock.Children.Add(elapsedBlock);

            var previewBlock = new TextBlock
            {
                Text = preview,
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
                FontSize = 10,
                FontFamily = new("Consolas"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new(12, 0, 0, 0)
            };
            dock.Children.Add(previewBlock);

            return dock;
        }
        catch { return null; }
    }
}
