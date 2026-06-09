using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using LTAI.Core.Rendering;

namespace LTAI.Desktop.Dialogs;

public sealed class ConfirmDialog : Window
{
    private ConfirmChoice? _result;

    public ConfirmDialog(string title, string message)
    {
        Title = title;
        Width = 480;
        Height = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowDecorations = Avalonia.Controls.WindowDecorations.BorderOnly;

        var panel = new StackPanel { Spacing = 12, Margin = new(20) };

        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
        });

        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
        });

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        void AddBtn(string label, ConfirmChoice choice, Color accent)
        {
            var btn = new Button
            {
                Content = label,
                Width = 100,
                Height = 32,
                Background = LtaiTheme.Sbb(accent),
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent),
                BorderThickness = new(0),
                CornerRadius = LtaiTheme.Radius.Sm,
                Cursor = new(StandardCursorType.Hand),
            };
            btn.Click += (_, _) => { _result = choice; Close(); };
            btnRow.Children.Add(btn);
        }

        AddBtn("确认 (Y)", ConfirmChoice.Yes, LtaiTheme.AccentSystem);
        AddBtn("拒绝 (N)", ConfirmChoice.No, LtaiTheme.AccentDanger);
        AddBtn("始终允许 (A)", ConfirmChoice.Always, LtaiTheme.AccentWarning);

        panel.Children.Add(btnRow);
        Content = panel;

        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Y) { _result = ConfirmChoice.Yes; Close(); }
            else if (e.Key == Avalonia.Input.Key.N) { _result = ConfirmChoice.No; Close(); }
            else if (e.Key == Avalonia.Input.Key.A) { _result = ConfirmChoice.Always; Close(); }
            else if (e.Key == Avalonia.Input.Key.Escape) { _result = ConfirmChoice.No; Close(); }
        };
    }

    public new async Task<ConfirmChoice> ShowDialog(Window owner)
    {
        await base.ShowDialog(owner);
        return _result ?? ConfirmChoice.No;
    }
}
