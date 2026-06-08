using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LTAI.Agent;
using LTAI.Agent.Vector;
using System.Text;

namespace LTAI.Desktop;

public sealed class GraphBrowserView : UserControl
{
    private readonly KbGraph? _graph;
    private readonly TextBlock _statusText;
    private readonly StackPanel _listPanel;

    public GraphBrowserView(KbGraph? graph = null, LTAI.Agent.Vector.KgStore? store = null)
    {
        _graph = graph;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new StackPanel { Margin = new(16), Spacing = 8 };

        root.Children.Add(new TextBlock
        { Text = "🔍 知识图谱浏览器", FontSize = 16, FontWeight = FontWeight.Bold,
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) });

        _statusText = new TextBlock { FontSize = 11, Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim) };
        root.Children.Add(_statusText);

        var searchRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var searchBox = new TextBox { PlaceholderText = "搜索图谱...", Width = 300 };
        searchBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) await DoSearch(searchBox.Text ?? "");
        };
        searchRow.Children.Add(searchBox);

        var searchBtn = new Button { Content = "🔎 搜索", Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent) };
        searchBtn.Click += async (_, _) => await DoSearch(searchBox.Text ?? "");
        searchRow.Children.Add(searchBtn);

        var statsBtn = new Button { Content = "📊 统计", Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) };
        statsBtn.Click += async (_, _) => await ShowStats(store);
        searchRow.Children.Add(statsBtn);
        root.Children.Add(searchRow);

        var scroll = new ScrollViewer();
        _listPanel = new StackPanel { Spacing = 4 };
        scroll.Content = _listPanel;
        root.Children.Add(scroll);

        Content = root;
        _statusText.Text = graph != null ? "就绪" : "⚠️ KbGraph 未可用";
    }

    private async Task DoSearch(string query)
    {
        _listPanel.Children.Clear();
        if (_graph == null || string.IsNullOrWhiteSpace(query)) return;

        _statusText.Text = "搜索中...";
        try
        {
            var results = await _graph.QueryAsync(query);
            _statusText.Text = $"找到 {results.Count} 个结果";
            foreach (var r in results)
            {
                var card = new Border
                {
                    Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
                    BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
                    BorderThickness = new(1),
                    CornerRadius = LtaiTheme.Radius.Sm,
                    Padding = new(8, 4),
                    Margin = new(0, 2),
                    Child = new TextBlock
                    {
                        Text = r,
                        FontSize = 11,
                        Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
                        TextWrapping = TextWrapping.Wrap,
                    }
                };
                _listPanel.Children.Add(card);
            }
        }
        catch (Exception ex) { _statusText.Text = $"错误: {ex.Message}"; }
    }

    private static async Task ShowStats(LTAI.Agent.Vector.KgStore? store)
    {
        if (store == null) return;
        try
        {
            var stats = await store.Stats();
            var dialog = new Window
            {
                Title = "图谱统计",
                Content = new TextBlock
                {
                    Text = stats,
                    FontSize = 12,
                    Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
                    Margin = new(16),
                },
                Width = 400, Height = 200,
                Background = LtaiTheme.Sbb(LtaiTheme.Bg),
            };
            var owner = GetMainWindow();
            if (owner != null) await dialog.ShowDialog(owner);
        }
        catch { }
    }

    private static Window? GetMainWindow()
    {
        var app = Application.Current;
        return app?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime
            ? lifetime.MainWindow
            : null;
    }
}
