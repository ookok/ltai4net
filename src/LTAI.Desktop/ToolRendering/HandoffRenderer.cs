using Avalonia.Controls;
using Avalonia.Media;

namespace LTAI.Desktop.ToolRendering;

public sealed class HandoffRenderer : IToolResultRenderer
{
    public bool CanRender(string token) => token.StartsWith("HANDOFF TO ");

    public Control? Render(string token, string? context = null)
    {
        return new TextBlock
        {
            Text = $"🔄 {token}",
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
            FontSize = 11,
            FontStyle = FontStyle.Italic
        };
    }
}
