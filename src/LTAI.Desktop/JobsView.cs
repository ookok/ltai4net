using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LTAI.Desktop.ViewModels;

namespace LTAI.Desktop;

public sealed class JobsView : UserControl
{
    private readonly JobsViewModel _vm;
    private readonly DispatcherTimer _refreshTimer;
    private readonly StackPanel _rowsPanel;
    private readonly TextBlock _emptyText;
    private readonly TextBlock _footerText;
    private readonly ScrollViewer _scroll;

    public JobsView(JobsViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new DockPanel();

        var header = new TextBlock
        {
            Text = "🛠 作业列表", FontSize = 16, FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary), Margin = new(0, 0, 0, 8)
        };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        _footerText = new TextBlock { Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim), FontSize = 11 };
        _footerText.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("FooterText"));
        DockPanel.SetDock(_footerText, Dock.Bottom);
        root.Children.Add(_footerText);

        _emptyText = new TextBlock
        {
            Text = "暂无作业", Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Margin = new(0, 20, 0, 0)
        };
        root.Children.Add(_emptyText);

        _scroll = new ScrollViewer { Margin = new(0, 4, 0, 0) };
        _rowsPanel = new StackPanel { Spacing = 2 };
        _scroll.Content = _rowsPanel;
        root.Children.Add(_scroll);

        Content = root;

        RefreshList();
        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => RefreshList());
        _refreshTimer.Start();
    }

    private void RefreshList()
    {
        _vm.Refresh();
        _rowsPanel.Children.Clear();
        _emptyText.IsVisible = _vm.Jobs.Count == 0;
        foreach (var job in _vm.Jobs)
            _rowsPanel.Children.Add(BuildRow(job));
    }

    private Grid BuildRow(JobsViewModel.JobItem j)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("40,80,*,80,80"), Margin = new(0, 1) };
        var bg = j.Status switch
        {
            "running" => LtaiTheme.Sbb(LtaiTheme.BgPanel),
            "completed" => LtaiTheme.Sbb(new Color(20, 30, 50, 30)),
            "failed" => LtaiTheme.Sbb(new Color(20, 80, 30, 30)),
            "cancelled" => LtaiTheme.Sbb(new Color(10, 80, 80, 80)),
            _ => LtaiTheme.Sbb(LtaiTheme.BgPanel),
        };
        grid.Background = bg;

        AddCell(grid, 0, j.Status switch
        {
            "running" => "⏳",
            "completed" => "✅",
            "failed" => "❌",
            "cancelled" => "⏹️",
            _ => "•"
        });
        AddCell(grid, 1, j.Id);
        AddCell(grid, 2, (j.Command?.Length > 50 ? j.Command[..50] + "..." : j.Command) ?? "");
        AddCell(grid, 3, j.ExitCode ?? (j.IsRunning ? "..." : "—"));

        if (j.IsRunning)
        {
            var cancelBtn = new Button { Content = "取消", FontSize = 10, Height = 18, Width = 40 };
            cancelBtn.Click += (_, _) => _vm.CancelJobCommand.Execute(j.Id);
            Grid.SetColumn(cancelBtn, 4);
            grid.Children.Add(cancelBtn);
        }
        else
            AddCell(grid, 4, "");

        return grid;
    }

    private static void AddCell(Grid grid, int col, string text)
    {
        var tb = new TextBlock
        {
            Text = text, FontSize = 11, FontFamily = LtaiTheme.CodeFont,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center, Margin = new(4, 2)
        };
        Grid.SetColumn(tb, col);
        grid.Children.Add(tb);
    }
}
