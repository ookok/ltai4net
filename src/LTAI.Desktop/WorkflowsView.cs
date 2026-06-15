using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LTAI.Desktop.ViewModels;
using LTAI.Agent.Workflows;

namespace LTAI.Desktop;

public sealed class WorkflowsView : UserControl
{
    private readonly WorkflowsViewModel _vm;
    private readonly StackPanel _listPanel;
    private readonly TextBlock _statusText;
    private readonly TextBlock _lastReloadText;
    private readonly DispatcherTimer _refreshTimer;

    private readonly YAMLWorkflowRegistry? _registry;

    public WorkflowsView(WorkflowsViewModel vm, YAMLWorkflowRegistry? registry = null)
    {
        _vm = vm;
        _registry = registry;
        DataContext = vm;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new StackPanel { Margin = new(16), Spacing = 8 };

        root.Children.Add(new TextBlock
        { Text = "🔁 工作流管理", FontSize = 16, FontWeight = FontWeight.Bold,
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) });

        _statusText = new TextBlock { FontSize = 11, Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim) };
        _statusText.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(_vm.StatusText)));
        root.Children.Add(_statusText);

        _lastReloadText = new TextBlock { FontSize = 10, Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim) };
        _lastReloadText.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(_vm.LastReloadText)));
        root.Children.Add(_lastReloadText);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var reloadBtn = new Button
        { Content = "🔄 重新加载", Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent) };
        reloadBtn.Click += async (_, _) => await _vm.ReloadAllCommand.ExecuteAsync(null);
        btnRow.Children.Add(reloadBtn);

        var createBtn = new Button
        { Content = "➕ 新建", Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent) };
        createBtn.Click += (_, _) => PromptCreateWorkflow();
        btnRow.Children.Add(createBtn);

        var goDevUiBtn = new Button
        { Content = "🔗 打开 DevUI", Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
          BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border), BorderThickness = new(1) };
        goDevUiBtn.Click += (_, _) => NavigateToDevUI();
        btnRow.Children.Add(goDevUiBtn);
        root.Children.Add(btnRow);

        var scroll = new ScrollViewer();
        _listPanel = new StackPanel { Spacing = 4 };
        scroll.Content = _listPanel;
        root.Children.Add(scroll);

        Content = root;
        RefreshList();
        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background,
            (_, _) => RefreshList());
        _refreshTimer.Start();
        DetachedFromVisualTree += (_, _) => _refreshTimer?.Stop();
    }

    private void RefreshList()
    {
        _listPanel.Children.Clear();
        if (_vm?.Workflows == null) return;
        foreach (var wf in _vm.Workflows)
        {
            var card = new Border
            {
                Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
                BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
                BorderThickness = new(1),
                CornerRadius = LtaiTheme.Radius.Sm,
                Padding = new(8, 4),
                Child = new TextBlock
                {
                    Text = $"[{wf.Type}] {wf.Name} (v{wf.Version})",
                    FontSize = 12,
                    Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
                }
            };
            _listPanel.Children.Add(card);
        }
    }

    private void PromptCreateWorkflow()
    {
        var dialog = new TextBox { PlaceholderText = "输入 workflow 名称..." };
        var win = new Window
        {
            Title = "新建 Workflow",
            Content = dialog,
            Width = 400, Height = 120,
            Background = LtaiTheme.Sbb(LtaiTheme.Bg),
        };
        dialog.KeyDown += async (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter && !string.IsNullOrWhiteSpace(dialog.Text))
            {
                win.Close();
                var dir = _registry?.WatchDirectory ?? Path.Combine(Environment.CurrentDirectory, ".livingtree", "workflows");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, dialog.Text + ".yaml");
                var template = $"kind: Workflow\nname: {dialog.Text}\ntype: sequential\nsteps:\n  - handoff:\n      agent: LTAI-Chat\n      input: \"{{{{input}}}}\"\n";
                try
                {
                    using var fs = new FileStream(path, FileMode.CreateNew);
                    await fs.WriteAsync(System.Text.Encoding.UTF8.GetBytes(template));
                    _statusText.Text = $"✅ 已创建 {dialog.Text}";
                    RefreshList();
                }
                catch (IOException) { _statusText.Text = $"❌ {dialog.Text} 已存在"; }
            }
        };
        if (VisualRoot is Window owner) win.ShowDialog(owner);
        else win.Show();
    }

    private static void NavigateToDevUI()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            { FileName = "http://localhost:5100/devui", UseShellExecute = true }); }
        catch { }
    }
}
