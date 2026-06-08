using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input;

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

            var accent = success ? LtaiTheme.AccentInfo : LtaiTheme.AccentDanger;
            var label = success ? "✅" : "❌";

            var panel = new StackPanel { Spacing = 4, Margin = new(0, 2) };

            var preview = output.Length > 80 ? output[..80] + "..." : output;
            var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

            var statusText = new TextBlock
            {
                Text = $"{label} {preview}",
                Foreground = LtaiTheme.Sbb(accent),
                FontFamily = LtaiTheme.CodeFont,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            };
            headerRow.Children.Add(statusText);

            if (output.Length > 80)
            {
                var expandBtn = new Button
                {
                    Content = "▼ 展开",
                    FontSize = 10,
                    Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
                    Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                    BorderThickness = new(0),
                    Cursor = new Cursor(StandardCursorType.Hand),
                };

                var fullContent = new TextBlock
                {
                    Text = output,
                    FontFamily = LtaiTheme.CodeFont,
                    FontSize = 11,
                    Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
                    TextWrapping = TextWrapping.Wrap,
                    IsVisible = false,
                };

                expandBtn.Click += (_, _) =>
                {
                    var expanded = !fullContent.IsVisible;
                    fullContent.IsVisible = expanded;
                    expandBtn.Content = expanded ? "▲ 折叠" : "▼ 展开";
                };
                headerRow.Children.Add(expandBtn);
                panel.Children.Add(headerRow);
                panel.Children.Add(fullContent);
            }
            else
            {
                panel.Children.Add(headerRow);
            }

            return panel;
        }
        catch
        {
            return null;
        }
    }
}
