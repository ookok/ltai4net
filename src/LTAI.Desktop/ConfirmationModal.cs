using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace LTAI.Desktop;

public sealed class ConfirmationModal : Window
{
    public bool Confirmed { get; private set; }
    public bool Always { get; private set; }

    public ConfirmationModal(string title, string message, string detail = "", bool showAlways = false)
    {
        Title = title;
        Width = 480;
        Height = 280;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Margin = new(24), Spacing = 16 };

        root.Children.Add(new TextBlock
        { Text = title, FontSize = 16, FontWeight = FontWeight.Bold,
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) });

        root.Children.Add(new TextBlock
        { Text = message, FontSize = 13, Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
          TextWrapping = TextWrapping.Wrap });

        if (!string.IsNullOrEmpty(detail))
            root.Children.Add(new TextBlock
            { Text = detail, FontSize = 11, Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
              TextWrapping = TextWrapping.Wrap });

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var yesBtn = new Button { Content = "✅ 是", Width = 80,
            Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent) };
        yesBtn.Click += (_, _) => { Confirmed = true; Close(); };
        btnRow.Children.Add(yesBtn);

        if (showAlways)
        {
            var alwaysBtn = new Button { Content = "🔄 总是", Width = 80,
                Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) };
            alwaysBtn.Click += (_, _) => { Confirmed = true; Always = true; Close(); };
            btnRow.Children.Add(alwaysBtn);
        }

        var noBtn = new Button { Content = "❌ 否", Width = 80,
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) };
        noBtn.Click += (_, _) => Close();
        btnRow.Children.Add(noBtn);
        root.Children.Add(btnRow);

        Content = root;
    }
}
