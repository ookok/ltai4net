using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace LTAI.Desktop;

public partial class MainWindow : Window
{
    private readonly Border _sidebar;
    private readonly ContentControl _contentArea;
    private readonly Button _collapseBtn;
    private readonly StackPanel _buttonStack;
    private bool _collapsed;
    private double _expandedWidth = 180;
    private readonly Grid _grid;
    private readonly TextBlock _statusBar;
    private readonly DispatcherTimer _statusTimer;

    private sealed record ViewEntry(string Name, string Shortcut, Control View);
    private readonly List<ViewEntry> _views;
    private int _activeIndex = 1;

    public MainWindow()
    {
        Title = "LTAI V0.56 — Production Hardening";
        Width = 1280;
        Height = 800;
        Background = new SolidColorBrush(Color.Parse("#0d1117"));

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ltai-icon.png");
        if (File.Exists(iconPath))
            Icon = new WindowIcon(iconPath);

        var svc = ServiceLocator.Get<LTAIService>();

        _views = new List<ViewEntry>
        {
            new("Dashboard",       "1", new DashboardView(svc)),
            new("Chat",            "2", new ChatView(svc)),
            new("LLM Config",      "3", new LLMConfigView(svc)),
            new("Pipeline",        "4", new PipelineView(svc)),
            new("Session",         "5", new SessionView(svc)),
            new("Diagnostics",     "6", new DiagnosticsView(svc)),
            new("Skill Workshop",  "7", new SkillWorkshopView(svc)),
            new("Prompt Lab",      "8", new PromptLabView(svc)),
            new("Task DAG",        "9", new TaskDagView(svc)),
            new("KG Explorer",     "0", new KnowledgeGraphExplorer(svc)),
            new("EIA Workbench",   "E", new EiaWorkbenchView(svc)),
        };

        _buttonStack = new StackPanel { Spacing = 2, Margin = new(4) };

        foreach (var (i, entry) in _views.Index())
        {
            var idx = i;
            var btn = new Button
            {
                Content = entry.Shortcut == "0" ? $" {entry.Shortcut}  {entry.Name}" : $" {entry.Shortcut}   {entry.Name}",
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = LtaiTheme.Sbb(Colors.Transparent),
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
                FontSize = 12,
                Height = 30,
                Padding = new(8, 0),
                BorderThickness = new(0),
                CornerRadius = new(4)
            };
            btn.PointerEntered += (_, _) =>
            {
                if (idx != _activeIndex)
                    btn.Background = LtaiTheme.Sbb(LtaiTheme.BgPanel);
            };
            btn.PointerExited += (_, _) =>
            {
                if (idx != _activeIndex)
                    btn.Background = LtaiTheme.Sbb(Colors.Transparent);
            };
            btn.Click += (_, _) => ActivateView(idx);
            _buttonStack.Children.Add(btn);
        }

        var spacer = new Border { Height = 8 };
        _buttonStack.Children.Add(spacer);

        _collapseBtn = new Button
        {
            Content = "<<",
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = LtaiTheme.Sbb(Colors.Transparent),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
            FontSize = 12,
            Height = 28,
            Padding = new(0),
            BorderThickness = new(0),
            CornerRadius = new(4)
        };
        _collapseBtn.Click += (_, _) => ToggleCollapse();
        _buttonStack.Children.Add(_collapseBtn);

        var scrollViewer = new ScrollViewer
        {
            Content = _buttonStack
        };

        _sidebar = new Border
        {
            Width = _expandedWidth,
            MinWidth = 48,
            MaxWidth = 300,
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            BorderThickness = new(0, 0, 1, 0),
            Child = scrollViewer,
            ClipToBounds = true
        };

        _contentArea = new ContentControl();

        var splitter = new GridSplitter
        {
            Width = 3,
            Background = LtaiTheme.Sbb(LtaiTheme.Border),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        _grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,3,*")
        };

        Grid.SetColumn(_sidebar, 0);
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(_contentArea, 2);

        _grid.Children.Add(_sidebar);
        _grid.Children.Add(splitter);
        _grid.Children.Add(_contentArea);

        _statusBar = new TextBlock
        {
            Text = "CPU: --  MEM: --",
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
            FontSize = 11,
            FontFamily = new("Consolas"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new(0, 0, 12, 4)
        };

        var rootPanel = new DockPanel();
        var statusBorder = new Border
        {
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            BorderThickness = new(0, 1, 0, 0),
            Child = _statusBar,
            Height = 24
        };
        DockPanel.SetDock(statusBorder, Dock.Bottom);
        rootPanel.Children.Add(statusBorder);
        rootPanel.Children.Add(_grid);

        Content = rootPanel;

        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => UpdateStatusBar());
        _statusTimer.Start();

        ActivateView(1);

        KeyDown += OnKeyDown;
        LtaiTheme.ThemeChanged += OnThemeChanged;
        DetachedFromVisualTree += (_, _) => LtaiTheme.ThemeChanged -= OnThemeChanged;
    }

    private void ActivateView(int index)
    {
        if (index < 0 || index >= _views.Count) return;
        _activeIndex = index;
        _contentArea.Content = _views[index].View;

        for (int i = 0; i < _buttonStack.Children.Count && i < _views.Count; i++)
        {
            if (_buttonStack.Children[i] is Button btn)
            {
                if (i == index)
                {
                    btn.Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA);
                    btn.Foreground = LtaiTheme.Sbb("#ffffff");
                }
                else
                {
                    btn.Background = LtaiTheme.Sbb(Colors.Transparent);
                    btn.Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary);
                }
            }
        }
    }

    private void ToggleCollapse()
    {
        _collapsed = !_collapsed;
        if (_collapsed)
        {
            _expandedWidth = _sidebar.Width;
            _sidebar.Width = 48;
            _collapseBtn.Content = ">>";
            foreach (var (i, entry) in _views.Index())
            {
                if (i < _buttonStack.Children.Count && _buttonStack.Children[i] is Button btn)
                {
                    var s = entry.Shortcut;
                    btn.Content = s.Length == 1 ? $" {s}" : s;
                    btn.HorizontalContentAlignment = HorizontalAlignment.Center;
                    btn.Padding = new(0);
                }
            }
        }
        else
        {
            _sidebar.Width = _expandedWidth;
            _collapseBtn.Content = "<<";
            foreach (var (i, entry) in _views.Index())
            {
                if (i < _buttonStack.Children.Count && _buttonStack.Children[i] is Button btn)
                {
                    btn.Content = entry.Shortcut == "0" ? $" {entry.Shortcut}  {entry.Name}" : $" {entry.Shortcut}   {entry.Name}";
                    btn.HorizontalContentAlignment = HorizontalAlignment.Left;
                    btn.Padding = new(8, 0);
                }
            }
        }
    }

    private void OnThemeChanged()
    {
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);
        _sidebar.Background = LtaiTheme.Sbb(LtaiTheme.BgPanel);
        _sidebar.BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border);
        _statusBar.Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim);
        ActivateView(_activeIndex);
    }

    private void UpdateStatusBar()
    {
        using var process = Process.GetCurrentProcess();
        var cpu = Environment.ProcessorCount;
        var mem = process.WorkingSet64 / 1024.0 / 1024.0;
        _statusBar.Text = string.Format("CPU: {0}c  MEM: {1:F0}MB", cpu, mem);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var handled = true;
        switch (e.KeyModifiers)
        {
            case KeyModifiers.Control:
                switch (e.Key)
                {
                    case Key.D1: ActivateView(0); break;
                    case Key.D2: ActivateView(1); break;
                    case Key.D3: ActivateView(2); break;
                    case Key.D4: ActivateView(3); break;
                    case Key.D5: ActivateView(4); break;
                    case Key.D6: ActivateView(5); break;
                    case Key.D7: ActivateView(6); break;
                    case Key.D8: ActivateView(7); break;
                    case Key.D9: ActivateView(8); break;
                    case Key.D0: ActivateView(9); break;
                    case Key.E: ActivateView(10); break;
                    case Key.T: LtaiTheme.Toggle(); break;
                    default: handled = false; break;
                }
                break;
            case KeyModifiers.None:
                if (e.Key == Key.Escape)
                {
                    ActivateView(1); // switch to Chat
                    var chatView = _contentArea.Content as ChatView;
                    chatView?.Cancel();
                }
                else handled = false;
                break;
            default: handled = false; break;
        }
        if (handled) e.Handled = true;
    }
}
