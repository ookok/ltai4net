using Avalonia.Controls;
using Avalonia.Media;

namespace LTAI.Desktop.Controls;

public sealed class UiButton : Button
{
    public UiButton()
    {
        Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA);
        Foreground = LtaiTheme.Sbb("#ffffff");
        FontWeight = FontWeight.Bold;
        FontSize = 12;
        Padding = new(12, 6);
        Margin = new(0, 2);
    }
}
