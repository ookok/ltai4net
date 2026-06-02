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
    private ChatView? _chatView;
    private readonly Border _sidebar;
    private readonly ContentControl _contentArea;
    private readonly Button _collapseBtn;
    private readonly StackPanel _buttonStack;
    private bool _collapsed;
    private double _expandedWidth = 180;
    private readonly Grid _grid;
    private readonly TextBlock _statusBar;
    private readonly DispatcherTimer _statusTimer;
    private readonly SessionStatsPanel _statsPanel;
    private DateTime _lastCpuSample = DateTime.UtcNow;
    private TimeSpan _lastCpuTime = TimeSpan.Zero;
    private static readonly Process _cachedProcess = Process.GetCurrentProcess();

    private sealed record ViewEntry(string Name, string Shortcut, Control View);
    private readonly List<ViewEntry> _views;
    private int _activeIndex = 1;

    public MainWindow(LTAIService svc)
    {
        Title = "LTAI — AI 助手";
        Width = 1280;
        Height = 800;
        Background = new SolidColorBrush(Color.Parse("#0d1117"));

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ltai-icon.png");
        if (File.Exists(iconPath))
            Icon = new WindowIcon(iconPath);

        var sessionManager = new SessionManager();
        var chatView = new ChatView(svc, sessionManager);
        _chatView = chatView;

        _views = new List<ViewEntry>
        {
            new("仪表盘", "1", new DashboardView(svc)),
            new("聊天",    "2", chatView),
            new("代码",    "3", CreateTextPadView(svc)),
            new("技能",    "4", new SkillsView()),
            new("配置",    "5", new ConfigView()),
            new("工作流",  "6", new WorkflowsView(svc)),
        };

        _buttonStack = new StackPanel { Spacing = 2, Margin = new(4) };

        foreach (var (i, entry) in _views.Index())
        {
            var idx = i;
            var btn = new Button
            {
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = LtaiTheme.Sbb(Colors.Transparent),
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
                FontSize = 12,
                Height = 30,
                Padding = new(8, 0),
                BorderThickness = new(0),
                CornerRadius = new(4)
            };
            var icons = new[] { "📊", "💬", "📝", "⚡", "⚙️", "🔁" };
            var icon = i < icons.Length ? icons[i] : "📄";
            var btnGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
            btnGrid.Children.Add(new TextBlock { Text = icon, Width = 22, Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary) });
            btnGrid.Children.Add(new TextBlock { Text = entry.Shortcut, Width = 16, Margin = new(0,0,2,0), Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim), FontFamily = new("Consolas") });
            Grid.SetColumn(btnGrid.Children[^1], 1);
            btnGrid.Children.Add(new TextBlock { Text = entry.Name, Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary) });
            Grid.SetColumn(btnGrid.Children[^1], 2);
            btn.Content = btnGrid;
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

        _statsPanel = new SessionStatsPanel(sessionManager);
        _statsPanel.SessionSelected += async name => { if (name != null) await chatView.LoadSessionAsync(name).ConfigureAwait(false); };
        _statsPanel.NewSessionClicked += async () => await chatView.ResetSessionAsync().ConfigureAwait(false);
        _buttonStack.Children.Add(_statsPanel);

        _collapseBtn = new Button
        {
            Content = "◀ 折叠",
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = LtaiTheme.Sbb(Colors.Transparent),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
            FontSize = 11,
            Height = 28,
            Padding = new(4, 0),
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

        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) =>
        {
            UpdateStatusBar();
            _statsPanel.Refresh();
        });
        _statusTimer.Start();

        // First-run setup: if no API keys configured, prompt user
        Dispatcher.UIThread.InvokeAsync(async () => await ShowSetupIfNeededAsync());

        ActivateView(1);

        KeyDown += OnKeyDown;
        LtaiTheme.ThemeChanged += OnThemeChanged;
        DetachedFromVisualTree += (_, _) => LtaiTheme.ThemeChanged -= OnThemeChanged;
    }

    private TextPadView CreateTextPadView(LTAIService svc)
    {
        var tp = new TextPadView(svc.Options.ResolveDataPath("../.."));
        tp.AskAiRequested += context =>
        {
            ActivateView(1);
            _chatView?.SendContentAsync($"分析以下文件/目录:\n{context}\n\n请查看并给出建议。");
        };
        return tp;
    }

    private async Task ShowSetupIfNeededAsync()
    {
        // Check if any providers have API keys
        var hasKey = App.Router?.RegisteredProviders.Any() == true;
        if (hasKey) return;

        // Check env vars directly (in case keys exist but weren't registered at startup)
        var knownVars = LTAI.Core.Configuration.KnownKeys.All.Select(k => k.EnvVar).ToArray();
        if (knownVars.Any(v => LTAI.Core.Configuration.SecretManager.Has(v)))
            return; // Keys exist — restart would register them

        // Show setup dialog
        var dialog = new Window
        {
            Title = "LTAI — First Run Setup",
            Width = 500,
            Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var stack = new StackPanel { Spacing = 10, Margin = new(20) };
        stack.Children.Add(new TextBlock
        {
            Text = "No API keys detected. Enter an API key to get started.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            Foreground = Brushes.White,
        });

        var providerBox = new ComboBox
        {
            ItemsSource = new[] { "DeepSeek", "OpenAI", "SiliconFlow", "Aliyun (Qwen)", "Zhipu (GLM)", "Groq", "Other (custom)" },
            SelectedIndex = 0,
            Margin = new(0, 10),
        };
        stack.Children.Add(providerBox);

        var keyBox = new TextBox
        {
            PlaceholderText = "Paste your API key here...",
            Margin = new(0, 5),
        };
        stack.Children.Add(keyBox);

        var envVarLabel = new TextBlock
        {
            Text = "Environment variable: DEEPSEEK_API_KEY",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#888888")),
        };
        stack.Children.Add(envVarLabel);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        var skipBtn = new Button { Content = "Skip", Width = 80 };
        var saveBtn = new Button { Content = "Save & Continue", Width = 120, Classes = { "accent" } };

        providerBox.SelectionChanged += (_, _) =>
        {
            envVarLabel.Text = providerBox.SelectedItem?.ToString() switch
            {
                "DeepSeek" => "Environment variable: DEEPSEEK_API_KEY",
                "OpenAI" => "Environment variable: OPENAI_API_KEY",
                "SiliconFlow" => "Environment variable: SILICONFLOW_API_KEY",
                "Aliyun (Qwen)" => "Environment variable: DASHSCOPE_API_KEY",
                "Zhipu (GLM)" => "Environment variable: ZHIPU_API_KEY",
                "Groq" => "Environment variable: GROQ_API_KEY",
                "Other (custom)" => "Enter custom variable name below:",
                _ => ""
            };
        };

        saveBtn.Click += (_, _) =>
        {
            var providerName = providerBox.SelectedItem?.ToString() ?? "DeepSeek";
            var envVar = providerName switch
            {
                "DeepSeek" => "DEEPSEEK_API_KEY",
                "OpenAI" => "OPENAI_API_KEY",
                "SiliconFlow" => "SILICONFLOW_API_KEY",
                "Aliyun (Qwen)" => "DASHSCOPE_API_KEY",
                "Zhipu (GLM)" => "ZHIPU_API_KEY",
                "Groq" => "GROQ_API_KEY",
                _ => "DEEPSEEK_API_KEY"
            };
            var key = keyBox.Text?.Trim();
            if (!string.IsNullOrEmpty(key))
            {
                LTAI.Core.Configuration.SecretManager.Set(envVar, key);

                // Register with router dynamically
                if (App.Router != null && App.HttpFactory != null)
                {
                    var endpoints = new Dictionary<string, (string ep, string model)>
                    {
                        ["DeepSeek"] = ("https://api.deepseek.com/v1", "deepseek-chat"),
                        ["OpenAI"] = ("https://api.openai.com/v1", "gpt-4o"),
                        ["SiliconFlow"] = ("https://api.siliconflow.cn/v1", "deepseek-ai/DeepSeek-V2.5"),
                        ["Aliyun (Qwen)"] = ("https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus"),
                        ["Zhipu (GLM)"] = ("https://open.bigmodel.cn/api/paas/v4", "glm-4-plus"),
                        ["Groq"] = ("https://api.groq.com/openai/v1", "llama-3.3-70b-versatile"),
                    };
                    if (endpoints.TryGetValue(providerName, out var ep))
                    {
                        var client = LTAI.AI.OpenAIChatClientFactory.Create(ep.ep, ep.model, key);
                        App.Router.Register(providerName, client);
                        App.Router.ActiveProvider = providerName;
                    }
                }
            }
            dialog.Close();
        };

        skipBtn.Click += (_, _) => dialog.Close();

        btnPanel.Children.Add(skipBtn);
        btnPanel.Children.Add(saveBtn);
        stack.Children.Add(btnPanel);
        dialog.Content = stack;

        await dialog.ShowDialog(this).ConfigureAwait(false);
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
            _collapseBtn.Content = "▶ 展开";
            foreach (var (i, entry) in _views.Index())
            {
                if (i < _buttonStack.Children.Count && _buttonStack.Children[i] is Button btn)
                {
                    var icons = new[] { "📊", "💬", "📝", "⚡", "⚙️", "🔁" };
                    var icon = i < icons.Length ? icons[i] : "📄";
                    btn.Content = $" {icon}";
                    btn.HorizontalContentAlignment = HorizontalAlignment.Center;
                    btn.Padding = new(0);
                }
            }
        }
        else
        {
            _sidebar.Width = _expandedWidth;
            _collapseBtn.Content = "◀ 折叠";
            foreach (var (i, entry) in _views.Index())
            {
                if (i < _buttonStack.Children.Count && _buttonStack.Children[i] is Button btn)
                {
                    var icons = new[] { "📊", "💬", "📝", "⚡", "⚙️" };
                    var icon = i < icons.Length ? icons[i] : "📄";
                    var btnGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
                    btnGrid.Children.Add(new TextBlock { Text = icon, Width = 22, Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary) });
                    btnGrid.Children.Add(new TextBlock { Text = entry.Shortcut, Width = 16, Margin = new(0,0,2,0), Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim), FontFamily = new("Consolas") });
                    Grid.SetColumn(btnGrid.Children[^1], 1);
                    btnGrid.Children.Add(new TextBlock { Text = entry.Name, Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary) });
                    Grid.SetColumn(btnGrid.Children[^1], 2);
                    btn.Content = btnGrid;
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
        _cachedProcess.Refresh();
        var now = DateTime.UtcNow;
        var cpuTime = _cachedProcess.TotalProcessorTime;
        var elapsed = (now - _lastCpuSample).TotalSeconds;
        var cpuUsage = elapsed > 0.5
            ? (cpuTime - _lastCpuTime).TotalSeconds / (Environment.ProcessorCount * elapsed) * 100
            : 0.0;
        _lastCpuSample = now;
        _lastCpuTime = cpuTime;
        var mem = _cachedProcess.WorkingSet64 / 1024.0 / 1024.0;
        _statusBar.Text = string.Format("CPU: {0:F1}%  MEM: {1:F0}MB", cpuUsage, mem);
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
            case KeyModifiers.Control | KeyModifiers.Shift:
                switch (e.Key)
                {
                    case Key.D: ActivateView(11); break;
                    case Key.V: ActivateView(12); break;
                    case Key.W: ActivateView(13); break;
                    case Key.J: ActivateView(14); break;
                    case Key.O: ActivateView(15); break;
                    default: handled = false; break;
                }
                break;
            case KeyModifiers.None:
                if (e.Key == Key.Escape)
                {
                    ActivateView(1); // switch to Chat
                    _chatView = _contentArea.Content as ChatView;
                    _chatView?.Cancel();
                }
                else handled = false;
                break;
            default: handled = false; break;
        }
        if (handled) e.Handled = true;
    }
}

/// <summary>Placeholder for views not yet implemented (Code, Config).</summary>
public sealed class StubView : UserControl
{
    private readonly TextBlock _text;

    public StubView(string title, object? svc = null)
    {
        _text = new TextBlock
        {
            Text = $"# {title}\n\n视图尚未实现。\n\n可用快捷键:\n- Ctrl+1: 仪表盘\n- Ctrl+2: 聊天\n- Ctrl+3: 代码\n- Ctrl+4: 技能\n- Ctrl+5: {title}",
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Margin = new(20),
        };
        Content = new ScrollViewer { Content = _text };
    }
}
