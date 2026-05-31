using Avalonia.Controls;
using Avalonia.Media;

namespace LTAI.Desktop.Controls;

public sealed class UiCard : Border
{
    public UiCard()
    {
        Background = LtaiTheme.Sbb(LtaiTheme.BgPanel);
        BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border);
        BorderThickness = new(1);
        CornerRadius = new(6);
        Padding = new(12);
        Margin = new(0, 4);
    }
}
