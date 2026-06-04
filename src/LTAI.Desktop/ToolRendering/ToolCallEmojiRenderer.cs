using Avalonia.Controls;
using Avalonia.Media;

namespace LTAI.Desktop.ToolRendering;

public sealed class ToolCallEmojiRenderer : IToolResultRenderer
{
    public bool CanRender(string token) => token.Contains("\uD83D\uDCCB");

    public Control? Render(string token, string? context = null)
    {
        var toolName = token.Replace("\uD83D\uDCCB", "").Trim();
        if (string.IsNullOrWhiteSpace(toolName)) toolName = "tool";

        var dock = new DockPanel { Margin = new(0, 1) };
        dock.Children.Add(new TextBlock
        {
            Text = $"🔧 {toolName}",
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
            FontFamily = LtaiTheme.CodeFont,
            FontSize = 11
        });

        var progressBar = new ProgressBar
        {
            IsIndeterminate = true,
            Width = 80,
            Height = 4,
            Margin = new(8, 0, 0, 0)
        };
        DockPanel.SetDock(progressBar, Dock.Right);
        dock.Children.Add(progressBar);

        return dock;
    }
}
