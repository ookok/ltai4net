using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LTAI.Agent.Memory;

namespace LTAI.Desktop;

public sealed class MemoryView : UserControl
{
    private readonly PalaceStore? _store;
    private readonly StackPanel _listPanel;
    private readonly TextBlock _statusText;
    private string _currentFilter = "";

    public MemoryView(PalaceStore? store = null)
    {
        _store = store;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new StackPanel { Margin = new(16), Spacing = 8 };

        root.Children.Add(new TextBlock
        { Text = "🧠 记忆浏览器", FontSize = 16, FontWeight = FontWeight.Bold,
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) });

        _statusText = new TextBlock { FontSize = 11, Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim) };
        root.Children.Add(_statusText);

        var searchRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var searchBox = new TextBox { PlaceholderText = "搜索记忆...", Width = 300 };
        searchBox.TextChanged += (_, _) => { _currentFilter = searchBox.Text ?? ""; RefreshList(); };
        searchRow.Children.Add(searchBox);

        var statsBtn = new Button { Content = "📊 统计", Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) };
        statsBtn.Click += async (_, _) => await ShowStatsAsync();
        searchRow.Children.Add(statsBtn);
        root.Children.Add(searchRow);

        var scroll = new ScrollViewer();
        _listPanel = new StackPanel { Spacing = 4 };
        scroll.Content = _listPanel;
        root.Children.Add(scroll);

        Content = root;
        RefreshList();
    }

    private void RefreshList()
    {
        _listPanel.Children.Clear();
        if (_store == null) { _statusText.Text = "⚠️ PalaceStore 未可用"; return; }

        try
        {
            var wings = _store.ListWings();
            _statusText.Text = $"{_store.Count()} 条记忆, {wings.Count} 个 wings";
            var count = 0;

            var allDrawers = _store.GetAllDrawers();
            foreach (var drawer in allDrawers)
            {
                var desc = $"[{drawer.Wing}/{drawer.Room}] {drawer.DrawerId[..Math.Min(drawer.DrawerId.Length, 60)]}";
                if (!string.IsNullOrEmpty(_currentFilter) &&
                    !desc.Contains(_currentFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (count++ >= 50) break;

                var card = new Border
                {
                    Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
                    BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
                    BorderThickness = new(1),
                    CornerRadius = LtaiTheme.Radius.Sm,
                    Padding = new(8, 4),
                    Child = new TextBlock
                    {
                        Text = desc,
                        FontSize = 11,
                        Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
                        TextWrapping = TextWrapping.Wrap,
                    }
                };
                _listPanel.Children.Add(card);
            }

            if (count == 0)
                _statusText.Text = "没有匹配的记忆";
            else if (count >= 50)
                _statusText.Text += " (显示前50条)";
        }
        catch (Exception ex) { _statusText.Text = $"错误: {ex.Message}"; }
    }

    private async Task ShowStatsAsync()
    {
        if (_store == null) return;
        try
        {
            var wings = _store.ListWings();
            var content = $"Wings: {wings.Count}\nDrawers: {_store.Count()}\n";
            var dialog = new Window
            {
                Title = "记忆统计",
                Content = new TextBlock
                {
                    Text = content,
                    FontSize = 12,
                    Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
                    Margin = new(16),
                    Background = LtaiTheme.Sbb(LtaiTheme.Bg),
                },
                Width = 400, Height = 200,
                Background = LtaiTheme.Sbb(LtaiTheme.Bg),
            };
            if (VisualRoot is Window owner)
                await dialog.ShowDialog(owner);
        }
        catch (Exception) { }
    }
}
