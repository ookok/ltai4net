using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using LTAI.Desktop.ViewModels;
using LTAI.Core.Session;

namespace LTAI.Desktop;

public sealed class SessionStatsPanel : UserControl
{
    private readonly SessionStatsViewModel _vm;
    private readonly StackPanel _sessionPanel;
    private readonly TextBlock _statsText;
    private readonly StackPanel _root;

    public event Action<string?>? SessionSelected;
    public event Action? NewSessionClicked;

    public SessionStatsPanel(SessionManager sessions)
    {
        _vm = new SessionStatsViewModel(sessions);
        DataContext = _vm;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);
        _sessionPanel = new StackPanel { Spacing = 2 };

        _root = new StackPanel { Spacing = 4 };

        _statsText = new TextBlock { FontSize = 11, Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim), Margin = new(4, 0, 0, 0) };

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var toggleBtn = new Button { Content = "📋 会话", FontSize = 10, Height = 18,
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel), Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            BorderThickness = new(0) };
        toggleBtn.Click += (_, _) => { _vm.IsExpanded = !_vm.IsExpanded; UpdateVisibility(); };
        headerRow.Children.Add(toggleBtn);

        var newBtn = new Button { Content = "+", FontSize = 10, Height = 18, Width = 22,
            Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA), Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent),
            BorderThickness = new(0) };
        newBtn.Click += (_, _) => NewSessionClicked?.Invoke();
        headerRow.Children.Add(newBtn);

        headerRow.Children.Add(_statsText);
        _root.Children.Add(headerRow);

        var scroll = new ScrollViewer { MaxHeight = 300 };
        scroll.Content = _sessionPanel;
        _root.Children.Add(scroll);

        Content = _root;
    }

    public void Refresh()
    {
        _vm.Refresh();
        _sessionPanel.Children.Clear();
        _statsText.Text = _vm.TotalSessions > 0 ? $"共 {_vm.TotalSessions} 个会话" : "暂无会话";

        if (!_vm.IsExpanded) return;

        foreach (var group in _vm.Groups)
        {
            var groupHeader = new TextBlock
            {
                Text = group.Title, FontSize = 10,
                Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
                FontWeight = FontWeight.Bold, Margin = new(4, 4, 0, 0)
            };
            _sessionPanel.Children.Add(groupHeader);

            foreach (var item in group.Items)
            {
                var row = new Border
                {
                    Background = item.IsCurrent ? LtaiTheme.Sbb(new Color(30, 30, 167, 64)) : LtaiTheme.Sbb(LtaiTheme.BgPanel),
                    CornerRadius = LtaiTheme.Radius.Sm,
                    Padding = new(6, 2),
                    Margin = new(4, 1),
                };
                row.PointerPressed += (_, _) => SessionSelected?.Invoke(item.Name);

                var text = new TextBlock
                {
                    Text = $"{item.Time}  {item.Preview}",
                    FontSize = 11,
                    Foreground = LtaiTheme.Sbb(item.IsCurrent ? LtaiTheme.AccentDNA : LtaiTheme.TextPrimary),
                    TextWrapping = TextWrapping.Wrap,
                };
                row.Child = text;
                _sessionPanel.Children.Add(row);
            }
        }
    }

    private void UpdateVisibility() => Refresh();
}
