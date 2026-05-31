using Avalonia.Controls;
using Avalonia.Media;

namespace LTAI.Desktop.Controls;

public sealed class UiLabel : TextBlock
{
    public UiLabel()
    {
        Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary);
        FontSize = 13;
        TextWrapping = TextWrapping.Wrap;
    }
}
