using Avalonia.Controls;
using Avalonia.Media;

namespace LTAI.Desktop.ToolRendering;

public sealed class BudgetHintRenderer : IToolResultRenderer
{
    public bool CanRender(string token) =>
        token.StartsWith("[budget:") || token.StartsWith("[note:");

    public Control? Render(string token, string? context = null)
    {
        return new TextBlock
        {
            Text = $"💰 {token}",
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
            FontSize = 10,
            FontStyle = FontStyle.Italic,
            TextWrapping = TextWrapping.Wrap
        };
    }
}
