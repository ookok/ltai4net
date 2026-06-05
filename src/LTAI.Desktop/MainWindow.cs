using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LTAI.Desktop.ViewModels;

using LTAI.Core.Session;

namespace LTAI.Desktop;

public partial class MainWindow : Window
{
    private ChatView? _chatView;
    private TextPadView? _textPadView;
    private readonly Border _sidebar;
    private readonly ContentControl _contentArea = null!;
    private readonly Button _collapseBtn;
    private readonly Button _gearBtn;
    private bool _focusMode;
    private readonly StackPanel _buttonStack;
    private double _expandedWidth = 180;
    private readonly Grid _grid;
    private readonly GridSplitter _splitter = null!;
    private readonly TextBlock _statusLeft = null!;
    private readonly TextBlock _capsuleText = null!;
    private readonly TextBlock _statusRight = null!;
    private readonly DispatcherTimer _statusTimer;
    private readonly SessionStatsPanel _statsPanel;
    private readonly MainWindowViewModel _vm;

    private sealed record ViewEntry(string Name, string Shortcut, Control View);
    private readonly List<ViewEntry> _views = [];

    public MainWindow(LTAIService svc)
    {
        Title = "LTAI — AI 助手";
        Width = 1280;
        Height = 800;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ltai-icon.png");
        if (File.Exists(iconPath))
            Icon = new WindowIcon(iconPath);

        var sessionManager = new SessionManager();
        var llmClient = new Services.LlmClient(svc.Chat);
        var cmdService = new Services.DesktopCommandService();
        var chatVm = new ViewModels.ChatViewModel(llmClient, cmdService);
        var chatView = new ChatView(svc, sessionManager, chatVm);
        _chatView = chatView;

        _views.AddRange([
            new("DevUI", "1", new DevUIView()),
            new("聊天",    "2", chatView),
            new("代码",    "3", CreateTextPadView(svc)),
            new("技能",    "4", new SkillsView()),
            new("工作流",  "5", new WorkflowsView(svc)),
            new("作业",    "6", new JobsView(svc)),
        ]);

        _vm = new MainWindowViewModel(_views.Count);
        var vm = _vm;
        _vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(_vm.ActiveIndex):
                    var idx = _vm.ActiveIndex;
                    if (idx >= 0 && idx < _views.Count)
                        _contentArea.Content = _views[idx].View;
                    UpdateSidebarButtons();
                    break;
                case nameof(_vm.StatusRight):
                    _statusRight!.Text = _vm.StatusRight;
                    break;
                case nameof(_vm.StatusLeft):
                    _statusLeft!.Text = _vm.StatusLeft;
                    break;
                case nameof(_vm.CapsuleText):
                    _capsuleText!.Text = _vm.CapsuleText;
                    break;
                case nameof(_vm.SidebarCollapsed):
                    ApplyCollapseState();
                    break;
            }
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
                CornerRadius = LtaiTheme.Radius.Sm
            };
            var icons = new[] { "🔬", "💬", "📝", "⚡", "🔁", "🛠" };
            var icon = i < icons.Length ? icons[i] : "📄";
            var btnGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
            btnGrid.Children.Add(new TextBlock { Text = icon, Width = 22, Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary) });
            btnGrid.Children.Add(new TextBlock { Text = entry.Shortcut, Width = 16, Margin = new(0,0,2,0), Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim), FontFamily = LtaiTheme.CodeFont });
            Grid.SetColumn(btnGrid.Children[^1], 1);
            btnGrid.Children.Add(new TextBlock { Text = entry.Name, Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary) });
            Grid.SetColumn(btnGrid.Children[^1], 2);
            btn.Content = btnGrid;
            ToolTip.SetTip(btn, $"Ctrl+{entry.Shortcut} — {entry.Name}");
            btn.PointerEntered += (_, _) =>
            {
                if (idx != _vm.ActiveIndex)
                    btn.Background = LtaiTheme.Sbb(LtaiTheme.BgPanel);
            };
            btn.PointerExited += (_, _) =>
            {
                if (idx != _vm.ActiveIndex)
                    btn.Background = LtaiTheme.Sbb(Colors.Transparent);
            };
            btn.Click += (_, _) => _vm.TryActivate(idx);
            _buttonStack.Children.Add(btn);
        }

        var spacer = new Border { Height = 8 };
        _buttonStack.Children.Add(spacer);

        _gearBtn = new Button
        {
            Content = "⚙️  配置",
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = LtaiTheme.Sbb(Colors.Transparent),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 12,
            Height = 28,
            Padding = new(8, 0),
            BorderThickness = new(0),
            CornerRadius = LtaiTheme.Radius.Sm
        };
        ToolTip.SetTip(_gearBtn, "配置管理");
        _gearBtn.Click += async (_, _) =>
        {
            var dlg = new ConfigDialog();
            await dlg.ShowDialog(this);
        };
        _buttonStack.Children.Add(_gearBtn);

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
            CornerRadius = LtaiTheme.Radius.Sm
        };
        ToolTip.SetTip(_collapseBtn, "折叠/展开侧边栏");
        _collapseBtn.Click += (_, _) => _vm.ToggleSidebar();
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

        _splitter = new GridSplitter
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
        Grid.SetColumn(_splitter, 1);
        Grid.SetColumn(_contentArea, 2);

        _grid.Children.Add(_sidebar);
        _grid.Children.Add(_splitter);
        _grid.Children.Add(_contentArea);

        _statusLeft = new TextBlock
        {
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
            FontSize = 11,
            FontFamily = LtaiTheme.CodeFont,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new(8, 0, 0, 0)
        };
        _capsuleText = new TextBlock
        {
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            FontSize = 11,
            FontFamily = LtaiTheme.CodeFont,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };
        _statusRight = new TextBlock
        {
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
            FontSize = 11,
            FontFamily = LtaiTheme.CodeFont,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Margin = new(0, 0, 12, 0)
        };
        var statusDock = new DockPanel();
        DockPanel.SetDock(_statusRight, Dock.Right);
        statusDock.Children.Add(_statusRight);
        statusDock.Children.Add(_capsuleText);
        statusDock.Children.Add(_statusLeft);

        ToolTip.SetTip(statusDock, _vm.StatusTooltip);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_vm.StatusTooltip))
                ToolTip.SetTip(statusDock, _vm.StatusTooltip);
        };

        var rootPanel = new DockPanel();
        var statusBorder = new Border
        {
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            BorderThickness = new(0, 1, 0, 0),
            Child = statusDock,
            Height = 24
        };
        DockPanel.SetDock(statusBorder, Dock.Bottom);
        rootPanel.Children.Add(statusBorder);
        rootPanel.Children.Add(_grid);

        Content = rootPanel;

        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) =>
        {
            _vm.RefreshStatus();
            _statsPanel.Refresh();
            var model = App.Router?.ActiveProvider ?? "--";
            var tokens = _chatView?.Tokens ?? 0;
            var branch = _textPadView?.GitBranch;
            var errorDot = _textPadView?.HasPendingError == true ? " 🔴" : "";
            _vm.CapsuleText = $"🤖 {model} | 🔥 {tokens:N0} t | 🌿 {branch ?? "--"}{errorDot}";
        });
        _statusTimer.Start();

        // First-run setup: if no API keys configured, prompt user
        Dispatcher.UIThread.InvokeAsync(async () => await ShowSetupIfNeededAsync());

        _vm.ActiveIndex = 1;

        KeyDown += OnKeyDown;
        LtaiTheme.ThemeChanged += OnThemeChanged;
        DetachedFromVisualTree += (_, _) => LtaiTheme.ThemeChanged -= OnThemeChanged;
    }

    private TextPadView CreateTextPadView(LTAIService svc)
    {
        var bridge = App.Services?.GetService(typeof(LTAI.Desktop.Debugging.DebugBridge))
            as LTAI.Desktop.Debugging.DebugBridge;
        var tp = new TextPadView(svc.Options.ResolveDataPath("../.."), bridge);
        tp.AskAiRequested += prompt =>
        {
            _vm.ActiveIndex = 1;
            _chatView?.SendContentAsync(prompt);
        };
        _textPadView = tp;

        // Citation navigation: clicks on @file references in chat go to the file
        ChatMessageRenderer.OnNavigateToFile = (path, line) =>
        {
            _vm.ActiveIndex = 2;
            tp.OpenFileAndScrollTo(path, line);
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
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
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
                        ["DeepSeek"] = ("https://api.deepseek.com/v1", "deepseek-v4-flash"),
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

    private void UpdateSidebarButtons()
    {
        for (int i = 0; i < _buttonStack.Children.Count && i < _views.Count; i++)
        {
            if (_buttonStack.Children[i] is Button btn)
            {
                if (i == _vm.ActiveIndex)
                {
                    btn.Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA);
                    btn.Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent);
                    btn.BorderThickness = new Thickness(3, 0, 0, 0);
                    btn.BorderBrush = LtaiTheme.Sbb(LtaiTheme.AccentDNA);
                }
                else
                {
                    btn.Background = LtaiTheme.Sbb(Colors.Transparent);
                    btn.Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary);
                    btn.BorderThickness = new Thickness(0);
                }
            }
        }
    }

    private void ApplyCollapseState()
    {
        if (_vm.SidebarCollapsed)
        {
            _expandedWidth = _sidebar.Width;
            _sidebar.Width = 48;
            _collapseBtn.Content = "▶ 展开";
            foreach (var (i, entry) in _views.Index())
            {
                if (i < _buttonStack.Children.Count && _buttonStack.Children[i] is Button btn)
                {
                    var icons = new[] { "🔬", "💬", "📝", "⚡", "🔁", "🛠" };
                    var icon = i < icons.Length ? icons[i] : "📄";
                    btn.Content = $" {icon}";
                    btn.HorizontalContentAlignment = HorizontalAlignment.Center;
                    btn.Padding = new(0);
                }
            }
            _gearBtn.Content = "⚙️";
            _gearBtn.HorizontalContentAlignment = HorizontalAlignment.Center;
            _gearBtn.Padding = new(0);
        }
        else
        {
            _sidebar.Width = _expandedWidth;
            _collapseBtn.Content = "◀ 折叠";
            foreach (var (i, entry) in _views.Index())
            {
                if (i < _buttonStack.Children.Count && _buttonStack.Children[i] is Button btn)
                {
                    var icons = new[] { "🔬", "💬", "📝", "⚡", "🔁", "🛠" };
                    var icon = i < icons.Length ? icons[i] : "📄";
                    var btnGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
                    btnGrid.Children.Add(new TextBlock { Text = icon, Width = 22, Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary) });
                    btnGrid.Children.Add(new TextBlock { Text = entry.Shortcut, Width = 16, Margin = new(0,0,2,0), Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim), FontFamily = LtaiTheme.CodeFont });
                    Grid.SetColumn(btnGrid.Children[^1], 1);
                    btnGrid.Children.Add(new TextBlock { Text = entry.Name, Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary) });
                    Grid.SetColumn(btnGrid.Children[^1], 2);
                    btn.Content = btnGrid;
                    btn.HorizontalContentAlignment = HorizontalAlignment.Left;
                    btn.Padding = new(8, 0);
                }
            }
            _gearBtn.Content = "⚙️  配置";
            _gearBtn.HorizontalContentAlignment = HorizontalAlignment.Left;
            _gearBtn.Padding = new(8, 0);
        }
    }

    public void SwitchToView(int index) => _vm.TryActivate(index);

    private void ToggleFocusMode()
    {
        _focusMode = !_focusMode;
        var show = !_focusMode;
        _sidebar.IsVisible = show;
        _splitter.IsVisible = show;
    }

    private void ShowCommandPalette()
    {
        var items = new List<CommandPaletteItem>
        {
            new("切换到 DevUI", "Ctrl+1", "🔬", () => _vm.TryActivate(0)),
            new("切换到聊天",   "Ctrl+2", "💬", () => _vm.TryActivate(1)),
            new("切换到代码",   "Ctrl+3", "📝", () => _vm.TryActivate(2)),
            new("切换到技能",   "Ctrl+4", "⚡", () => _vm.TryActivate(3)),
            new("切换到工作流", "Ctrl+5", "🔁", () => _vm.TryActivate(4)),
            new("切换到作业",   "Ctrl+6", "🛠", () => _vm.TryActivate(5)),
            new("切换主题",     "Ctrl+T", "🎨", () => LtaiTheme.Toggle()),
            new("专注模式",     "Ctrl+.", "🎯", () => ToggleFocusMode()),
            new("开启配置",     "",        "⚙️", () => { var dlg = new ConfigDialog(); dlg.ShowDialog(this); }),
            new("新建会话",     "",        "➕", () => _chatView?.ResetSessionAsync()),
        };
        var dlg = new CommandPaletteDialog(items);
        dlg.ShowDialog(this);
    }

    private void OnThemeChanged()
    {
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);
        _sidebar.Background = LtaiTheme.Sbb(LtaiTheme.BgPanel);
        _sidebar.BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border);
        _statusLeft.Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim);
        _capsuleText.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDNA);
        _statusRight.Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim);
        _gearBtn.Background = LtaiTheme.Sbb(Colors.Transparent);
        _gearBtn.Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary);
        UpdateSidebarButtons();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var handled = true;
        switch (e.KeyModifiers)
        {
            case KeyModifiers.Control:
                switch (e.Key)
                {
                    case Key.D1: _vm.TryActivate(0); break;
                    case Key.D2: _vm.TryActivate(1); break;
                    case Key.D3: _vm.TryActivate(2); break;
                    case Key.D4: _vm.TryActivate(3); break;
                    case Key.D5: _vm.TryActivate(4); break;
                    case Key.D6: _vm.TryActivate(5); break;
                    case Key.D7: _vm.TryActivate(6); break;
                    case Key.D8: _vm.TryActivate(7); break;
                    case Key.D9: _vm.TryActivate(8); break;
                    case Key.D0: _vm.TryActivate(9); break;
                    case Key.E: _vm.TryActivate(10); break;
                    case Key.T: LtaiTheme.Toggle(); break;
                    case Key.OemPeriod: ToggleFocusMode(); break;
                    default: handled = false; break;
                }
                break;
            case KeyModifiers.Control | KeyModifiers.Shift:
                switch (e.Key)
                {
                    case Key.D: _vm.TryActivate(11); break;
                    case Key.V: _vm.TryActivate(12); break;
                    case Key.W: _vm.TryActivate(13); break;
                    case Key.J: _vm.TryActivate(14); break;
                    case Key.O: _vm.TryActivate(15); break;
                    case Key.P: ShowCommandPalette(); break;
                    default: handled = false; break;
                }
                break;
            case KeyModifiers.None:
                if (e.Key == Key.Escape)
                {
                    _vm.ActiveIndex = 1; // switch to Chat
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
