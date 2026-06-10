using System.Diagnostics;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.AI;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Reflection;
using LTAI.Core.Commands;
using LTAI.Core.Rendering;
using LTAI.Core.Session;

namespace LTAI.Desktop;

public sealed partial class ChatView : UserControl, IChatRenderer
{
    private readonly LTAIService _svc;
    private readonly TextBox _input;
    private readonly StackPanel _outputStack;
    private readonly ScrollViewer _scroller;
    private const int MaxVisibleMessages = 80;
    private readonly StackPanel _footerStats;
    private readonly Button _actionBtn;
    private readonly List<string> _history = [];
    private int _historyIdx = -1;
    private CancellationTokenSource? _cts;
    private int _turns, _tokens;
    public int Tokens => Volatile.Read(ref _tokens);
    public int Turns => Volatile.Read(ref _turns);
    private bool _isSending;
    private readonly SessionManager _sessionManager;
    private readonly LTAI.Desktop.ToolRendering.ToolResultRendererRegistry _toolRenderers;
    private readonly LTAI.Agent.Snippets.SnippetStore? _snippetStore;
    private TextBlock? _currentResponseText;
    private readonly ViewModels.ChatViewModel? _vm;
    private readonly Border _modeBar;
    private readonly TextBlock _modeText;
    private readonly TextBlock _todoText;

    private readonly Dictionary<int, string> _subSessions = new();
    private readonly Dictionary<int, Stopwatch> _subStartTimes = new();
    private System.Collections.Specialized.NotifyCollectionChangedEventHandler? _vmCollectionChanged;
    private System.ComponentModel.PropertyChangedEventHandler? _vmPropertyChanged;
    private Action? _vmExitRequested;

    public SessionManager SessionManager => _sessionManager;
    public ViewModels.ChatViewModel? ViewModel => _vm;

    public async Task LoadSessionAsync(string name)
    {
        var handle = await _sessionManager.LoadSessionAsync(name);
        if (handle == null) return;
        _outputStack.Children.Clear();

        // 子会话显示返回父会话按钮
        var sessions = _sessionManager.ListSessions();
        var sessionInfo = sessions.FirstOrDefault(s => s.Name == name);
        if (sessionInfo?.ParentId != null)
        {
            var parentName = sessionInfo.ParentId;
            var backBtn = new Button
            {
                Content = "🔙 返回父会话",
                FontSize = 11, Height = 22,
                Background = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent),
                BorderThickness = new(0), CornerRadius = LtaiTheme.Radius.Sm,
                Margin = new(0, 4),
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            backBtn.Click += async (_, _) => await LoadSessionAsync(parentName);
            _outputStack.Children.Add(new Border
            {
                Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
                BorderBrush = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
                BorderThickness = new(1), CornerRadius = LtaiTheme.Radius.Md,
                Padding = new(8), Margin = new(0, 0, 0, 6),
                Child = new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock { Text = "📋 子任务详情", FontWeight = FontWeight.Bold, FontSize = 12, Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo) },
                        new TextBlock { Text = "子 Agent 的完整对话记录", FontSize = 11, Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary) },
                        backBtn
                    }
                }
            });
        }

        foreach (var msg in handle.Messages)
        {
            var label = msg.Role == ChatRole.User ? "[You]" : "[LTAI]";
            var color = msg.Role == ChatRole.User ? LtaiTheme.ChatUser : LtaiTheme.AccentSystem;
            AddBubble(label, msg.Text ?? "", color, LtaiTheme.Border);
        }
        _turns = handle.Messages.Count / 2;
        RefreshStats();
    }

    public async Task ResetSessionAsync()
    {
        if (_sessionManager.CurrentHandle != null)
            await _sessionManager.SaveSessionAsync().ConfigureAwait(false);
        _sessionManager.NewSession();
        _outputStack.Children.Clear();
        _turns = 0;
        _tokens = 0;
        RefreshStats();
        AddSystemBubble("✅ 新会话已创建");
    }

    /// <summary>从外部设置输入文本并自动发送（用于"问 AI"功能）。</summary>
    public async Task SendContentAsync(string text)
    {
        if (_vm != null)
        {
            _vm.Input = text;
            await _vm.SendCommand.ExecuteAsync(null);
            return;
        }
        _input.Text = text;
        await SendAsync();
    }

    private readonly Services.DesktopCommandService _cmdService = new();

    public ChatView(LTAIService svc, SessionManager? sessionManager = null,
        ViewModels.ChatViewModel? viewModel = null)
    {
        _svc = svc;
        _sessionManager = sessionManager ?? new SessionManager();
        _toolRenderers = LTAI.Desktop.ToolRendering.DefaultRenderers.Create();
        _snippetStore = svc?.Services?.GetService(typeof(LTAI.Agent.Snippets.SnippetStore)) as LTAI.Agent.Snippets.SnippetStore;
        SetupQuestionHandler();

        // D4: ViewModel-driven command wiring — if a ViewModel is provided,
        // route send/cancel through it and render output from its Messages collection.
        _vm = viewModel;
        if (_vm != null)
            WireViewModel();

        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        // ── Root: Grid 3 rows (header auto / messages * / footer auto) ──
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new(8) };

        // ── Mode / Todo bar (Row 0) ──
        _modeText = new TextBlock
        {
            FontSize = 12,
            FontFamily = LtaiTheme.CodeFont,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        _todoText = new TextBlock
        {
            FontSize = 11,
            FontFamily = LtaiTheme.CodeFont,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim)
        };
        var modeStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _modeText, _todoText }
        };
        _modeBar = new Border
        {
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            BorderThickness = new(0, 0, 0, 1),
            Padding = new(8, 4),
            Child = modeStack,
            IsVisible = false
        };
        Grid.SetRow(_modeBar, 0);
        root.Children.Add(_modeBar);

        // ── Messages area (Row 1) ──
        _outputStack = new StackPanel { Spacing = 4 };
        _scroller = new ScrollViewer { Content = _outputStack };
        Grid.SetRow(_scroller, 1);
        root.Children.Add(_scroller);

        // ── Footer (stats + tools + input) ──
        _footerStats = new StackPanel { Spacing = 1 };
        var footerBorder = new Border
        {
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            BorderThickness = new(0, 1, 0, 0),
            Padding = new(0, 6, 0, 0),
        };
        var footerStack = new StackPanel();
        footerStack.Children.Add(_footerStats);

        var toolbox = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new(0, 6, 0, 0)
        };
        var fileBtn = new Button
        {
            Content = "Files", Width = 52,
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 11
        };
        fileBtn.Click += async (_, _) => await PickFilesAsync();
        var folderBtn = new Button
        {
            Content = "Folder", Width = 55,
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 11
        };
        folderBtn.Click += async (_, _) => await PickFolderAsync();
        toolbox.Children.Add(fileBtn);
        toolbox.Children.Add(folderBtn);

        _input = new TextBox
        {
            Name = "InputBox",
            PlaceholderText = "输入消息... Enter=发送, Shift+Enter=换行, Ctrl+Enter=发送, ↑↓=历史  |  拖入文件/文件夹",
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            Background = LtaiTheme.Sbb(LtaiTheme.BgInput),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            BorderThickness = new(1),
            FontFamily = LtaiTheme.CodeFont,
            MinHeight = 72,
            AcceptsReturn = false,
            TextWrapping = TextWrapping.Wrap
        };
        Avalonia.Automation.AutomationProperties.SetAutomationId(_input, "ChatInput");
        _input.KeyDown += OnInputKey;

        _actionBtn = new Button
        {
            Name = "SendButton",
            Content = "Send",
            Width = 60,
            Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent),
            FontWeight = FontWeight.Bold
        };
        _actionBtn.Click += (_, _) =>
        {
            if (_vm != null)
            {
                if (_vm.IsSending) _vm.CancelCommand.Execute(null);
                else
                {
                    _vm.Input = _input.Text ?? "";
                    _ = _vm.SendCommand.ExecuteAsync(null).ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                            System.Diagnostics.Debug.WriteLine($"[ChatView] SendCommand failed: {t.Exception?.InnerException?.Message}");
                    });
                }
                return;
            }
            if (_isSending) Cancel();
            else _ = SendAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                    System.Diagnostics.Debug.WriteLine($"[ChatView] SendAsync failed: {t.Exception?.InnerException?.Message}");
            });
        };

        var inputRow = new DockPanel { Margin = new(0, 4, 0, 0) };
        var btnPanel = new DockPanel { Margin = new(4, 0, 0, 0) };
        btnPanel.Children.Add(_actionBtn);
        DockPanel.SetDock(btnPanel, Dock.Right);
        inputRow.Children.Add(btnPanel);
        inputRow.Children.Add(_input);

        footerStack.Children.Add(toolbox);
        footerStack.Children.Add(inputRow);
        footerBorder.Child = footerStack;
        Grid.SetRow(footerBorder, 2);
        root.Children.Add(footerBorder);

        // Auto-load most recent session or start fresh
        var existing = _sessionManager.ListSessions();
        var handle = existing.Length > 0 ? _sessionManager.LoadSession(existing[0].Name) : null;
        if (handle != null)
        {
            foreach (var msg in handle.Messages)
            {
                var label = msg.Role == ChatRole.User ? "[You]" : "[LTAI]";
                var color = msg.Role == ChatRole.User ? LtaiTheme.ChatUser : LtaiTheme.AccentSystem;
                AddBubble(label, msg.Text ?? "", color, LtaiTheme.Border);
            }
            _turns = handle.Messages.Count / 2;
        }
        else
        {
            _sessionManager.NewSession();
            AddSuggestionCards();
        }

        _turns = 0;
        _tokens = 0;
        SetupDragDrop();
        Content = root;

        void OnThemeChanged()
        {
            Background = LtaiTheme.Sbb(LtaiTheme.Bg);
            _input.Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary);
            _input.Background = LtaiTheme.Sbb(LtaiTheme.BgInput);
            _actionBtn.Background = _isSending
                ? LtaiTheme.Sbb(LtaiTheme.AccentDanger)
                : LtaiTheme.Sbb(LtaiTheme.AccentDNA);
        }

        LtaiTheme.ThemeChanged += OnThemeChanged;
        EventHandler<Avalonia.VisualTreeAttachmentEventArgs>? detachedHandler = null;
        detachedHandler = (_, _) =>
        {
            LtaiTheme.ThemeChanged -= OnThemeChanged;
            LTAI.Agent.Tools.SubagentTools.OnSubagentMessage -= OnSubagentMessage;
            LTAI.Agent.Tools.SubagentTools.OnSubagentComplete -= OnSubagentComplete;
            if (_questionHandler != null)
            {
        var qs = _svc?.Services?.GetService(typeof(LTAI.Agent.Tools.QuestionService)) as LTAI.Agent.Tools.QuestionService;
                if (qs != null) qs.QuestionPosted -= _questionHandler;
            }
            if (_vm != null)
            {
                if (_vmCollectionChanged != null)
                    _vm.Messages.CollectionChanged -= _vmCollectionChanged;
                if (_vmPropertyChanged != null)
                    _vm.PropertyChanged -= _vmPropertyChanged;
                if (_vmExitRequested != null)
                    _vm.ExitRequested -= _vmExitRequested;
            }
            // Dispose CTS on detach
            if (_cts != null) { _cts.Cancel(); _cts.Dispose(); _cts = null; }
            DetachedFromVisualTree -= detachedHandler;
        };
        DetachedFromVisualTree += detachedHandler;
        LTAI.Agent.Tools.SubagentTools.OnSubagentMessage += OnSubagentMessage;
        LTAI.Agent.Tools.SubagentTools.OnSubagentComplete += OnSubagentComplete;

        RefreshStats();
    }

    public void Cancel()
    {
        if (_vm != null) { _vm.CancelCommand.Execute(null); return; }
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private void WireViewModel()
    {
        if (_vm == null) return;

        // Render new messages from ViewModel
        _vmCollectionChanged = (_, e) =>
        {
            if (e.NewItems == null) return;
            foreach (ViewModels.ChatMessage msg in e.NewItems)
            {
                if (msg.Role == "user")
                    AddBubble("[You]", msg.Text, LtaiTheme.ChatUser, LtaiTheme.Border);
                else if (msg.Role == "assistant")
                    AddBubble("[LTAI]", msg.Text, LtaiTheme.AccentSystem, LtaiTheme.Border);
                else
                    AddBubble("ℹ️", msg.Text, LtaiTheme.AccentInfo, LtaiTheme.Border);
            }
            _scroller.ScrollToEnd();
        };
        _vm.Messages.CollectionChanged += _vmCollectionChanged;

        // Clear textbox when ViewModel clears Input after send
        _vmPropertyChanged = (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModels.ChatViewModel.Input))
                Dispatcher.UIThread.Post(() => _input.Text = _vm.Input);
            if (e.PropertyName == nameof(ViewModels.ChatViewModel.IsSending))
                _actionBtn.Content = _vm.IsSending ? "Cancel" : "Send";
        };
        _vm.PropertyChanged += _vmPropertyChanged;

        // Close window on exit
        _vmExitRequested = () =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                var tl = TopLevel.GetTopLevel(this);
                if (tl is Window w) w.Close();
            });
        };
        _vm.ExitRequested += _vmExitRequested;
    }

    private void OnInputKey(object? s, KeyEventArgs e)
    {
        // Shift+Enter → insert newline
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Shift)
        {
            var idx = _input!.CaretIndex;
            var text = _input!.Text ?? "";
            _input!.Text = text[..idx] + "\n" + text[idx..];
            _input!.CaretIndex = idx + 1;
            e.Handled = true;
            return;
        }
        // Enter (plain or Ctrl) → send
        if (e.Key == Key.Enter && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Control)
        {
            if (_vm != null)
            {
                if (_vm.IsSending) return;
                e.Handled = true;
                _vm.Input = _input.Text ?? "";
                _ = _vm.SendCommand.ExecuteAsync(null).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        System.Diagnostics.Debug.WriteLine($"[ChatView] SendCommand failed: {t.Exception?.InnerException?.Message}");
                });
                return;
            }
            if (_isSending) return;
            e.Handled = true;
            _ = SendAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                    System.Diagnostics.Debug.WriteLine($"[ChatView] SendAsync failed: {t.Exception?.InnerException?.Message}");
            });
        }
        // Tab (input empty) → cycle agent mode
        else if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.None && string.IsNullOrEmpty(_input?.Text))
        {
            e.Handled = true;
            _ = CycleModeAsync();
        }
        else if (e.Key == Key.Up && e.KeyModifiers == KeyModifiers.None && _history.Count > 0)
        {
            if (_historyIdx == -1) _historyIdx = _history.Count - 1;
            else if (_historyIdx > 0) _historyIdx--;
            _input.Text = _history[_historyIdx];
            _input.CaretIndex = _input.Text.Length;
            e.Handled = true;
        }
        else if (e.Key == Key.Down && e.KeyModifiers == KeyModifiers.None && _historyIdx >= 0)
        {
            _historyIdx++;
            _input.Text = _historyIdx < _history.Count ? _history[_historyIdx] : "";
            _input.CaretIndex = _input.Text.Length;
            if (_historyIdx >= _history.Count) _historyIdx = -1;
            e.Handled = true;
        }
    }

    private void SetupDragDrop()
    {
        DragDrop.SetAllowDrop(_input, true);

        _input.AddHandler(DragDrop.DragEnterEvent, (_, e) =>
        {
            _input.BorderBrush = LtaiTheme.Sbb(LtaiTheme.AccentDNA);
            _input.BorderThickness = new Thickness(2);
        });

        _input.AddHandler(DragDrop.DragLeaveEvent, (_, e) =>
        {
            _input.BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border);
            _input.BorderThickness = new Thickness(1);
        });

        _input.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        });

        // Avalonia 12.0: Access drag data via DataObject.GetDataFromDragDropEvent
        _input.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            try
            {
                _input.BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border);
                _input.BorderThickness = new Thickness(1);
                // Avalonia 12.0: DragEventArgs doesn't expose Data directly.
                // Use DataObject.TryGetDataFromDropEvent or reflection fallback.
                var data = await GetDragDropDataAsync(e);
                if (data is IEnumerable<IStorageItem> files)
                {
                    e.Handled = true;
                    await ImportDroppedItems(files.ToList());
                }
            }
            catch { /* drag data unavailable */ }
        });
    }

    private static async Task<object?> GetDragDropDataAsync(DragEventArgs e)
    {
        // Avalonia 12.0: DragEventArgs.Data removed. Access via reflection.
        try
        {
            // Try common property names used across Avalonia 12.x versions
            foreach (var propName in new[] { "DataObject", "Data", "DragData" })
            {
                var prop = e.GetType().GetProperty(propName,
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                var val = prop?.GetValue(e);
                if (val == null) continue;

                // If it's already an IStorageItem enumerable
                if (val is IEnumerable<IStorageItem> items)
                    return items.ToList();

                // If it has GetFiles method (DataObject pattern)
                var getFiles = val.GetType().GetMethod("GetFiles");
                if (getFiles != null)
                {
                    var files = getFiles.Invoke(val, null);
                    if (files is IEnumerable<IStorageItem> storageItems)
                        return storageItems.ToList();
                }

                // If it has GetDataAsync (async DataObject pattern)
                var getDataAsync = val.GetType().GetMethod("GetDataAsync", [typeof(string)]);
                if (getDataAsync != null)
                {
                    var task = (Task)getDataAsync.Invoke(val, [DataFormat.File])!;
                    await task.ConfigureAwait(false);
                    var result = task.GetType().GetProperty("Result")?.GetValue(task);
                    if (result is IEnumerable<IStorageItem> asyncItems)
                        return asyncItems.ToList();
                }
            }
        }
        catch { /* data not accessible */ }
        return null;
    }

    private async Task ImportDroppedItems(List<IStorageItem> items)
    {
        var sb = new StringBuilder();
        foreach (var item in items)
        {
            try
            {
                var path = item.Path.LocalPath;
                if (Directory.Exists(path))
                {
                    sb.AppendLine($"@@\"{path}\"");
                }
                else if (File.Exists(path))
                {
                    var content = await File.ReadAllTextAsync(path);
                    var snippet = content.Length > 2000 ? content[..2000] + "\n...(truncated)" : content;
                    var name = Path.GetFileName(path);
                    sb.AppendLine($"@\"{path}\"");
                    sb.AppendLine($"{name}:");
                    sb.AppendLine(snippet);
                }
            }
            catch { /* user cancelled — OK */ }
        }
        if (sb.Length > 0)
        {
            if (_input.Text?.Length > 0) _input.Text += "\n";
            _input.Text += sb.ToString();
        }
    }

    private void SetSending(bool sending)
    {
        _isSending = sending;
        _actionBtn.Content = sending ? "Stop" : "Send";
        _actionBtn.Background = sending
            ? LtaiTheme.Sbb(LtaiTheme.AccentDanger)
            : LtaiTheme.Sbb(LtaiTheme.AccentDNA);
    }

    private async Task SendAsync()
    {
        var query = _input.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query)) return;

        // Handle slash commands
        if (query.StartsWith('/'))
        {
            var parts = query.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var cmdName = parts[0][1..].ToLowerInvariant();
            var args = parts.Length > 1 ? parts[1] : "";
            // 顶级命令无参数 → 级联选择器
            if (string.IsNullOrWhiteSpace(args) && CmdHasLevel1(cmdName))
            {
                await ShowCmdPickerAsync(cmdName);
                return;
            }
            _input.Text = "";
            await HandleSlashCommandAsync(query).ConfigureAwait(false);
            return;
        }

        _history.Add(query);
        if (_history.Count > 100) _history.RemoveRange(0, _history.Count - 100);
        _historyIdx = -1;
        _input.Text = "";
        Interlocked.Increment(ref _turns);
        SetSending(true);

        AddBubble("[You]", query, LtaiTheme.ChatUser, LtaiTheme.Border);

        var aiBubble = AddAIBubbleHeader();
        var aiContent = new StackPanel { Spacing = 4 };
        aiBubble.Children.Add(aiContent);

        var statusDots = new TextBlock
        {
            Text = "⚪",
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            FontSize = 16,
            Margin = new(4, 0)
        };
        var dotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        var dotFrames = new[] { "⚪", "⚫", "⚪" };
        var dotIdx = 0;
        dotTimer.Tick += (_, _) =>
        {
            dotIdx = (dotIdx + 1) % dotFrames.Length;
            Dispatcher.UIThread.Post(() => statusDots.Text = dotFrames[dotIdx]);
        };
        dotTimer.Start();
        aiContent.Children.Add(statusDots);
        var thinkPanel = new Border
        {
            Background = LtaiTheme.Sbb(LtaiTheme.ThinkBg),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            BorderThickness = new(1),
            CornerRadius = LtaiTheme.Radius.Md,
            Padding = new(6),
            Margin = new(0, 2),
            IsVisible = false
        };
        var thinkText = new SelectableTextBlock
        {
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
            FontFamily = LtaiTheme.CodeFont,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false
        };
        var thinkToggle = new TextBlock
        {
            Text = "▶  Thinking",
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
            FontSize = 10,
            FontStyle = FontStyle.Italic,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };
        var _expanded = false;
        thinkToggle.PointerPressed += (_, _) =>
        {
            _expanded = !_expanded;
            thinkToggle.Text = _expanded ? "▼  Thinking" : "▶  Thinking";
            thinkText.IsVisible = _expanded;
        };
        var thinkInner = new StackPanel
        {
            Children = { thinkToggle, thinkText }
        };
        thinkPanel.Child = thinkInner;
        aiContent.Children.Add(thinkPanel);

        var toolPanel = new StackPanel { Spacing = 2, IsVisible = false, Margin = new(0, 2) };
        var toolTitle = new TextBlock
        {
            Text = "Tools",
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            FontSize = 10,
            FontStyle = FontStyle.Italic,
            Margin = new(0, 0, 0, 2)
        };
        toolPanel.Children.Add(toolTitle);
        aiContent.Children.Add(toolPanel);

        var responsePanel = new StackPanel { Spacing = 2 };
        aiContent.Children.Add(responsePanel);
        _currentResponseText = null;

        Border? taskBanner = null;
        var firstTokenReceived = false;

        // A5: dispose previous CTS before reassignment to avoid timer leak
        if (_cts is { IsCancellationRequested: false }) { _cts.Cancel(); _cts.Dispose(); }
        _cts = new CancellationTokenSource();
        var responseBuf = new StringBuilder();
        var thinkBuf = new StringBuilder();
        var inThinking = false;
        var lastRenderedText = "";
        var lastUiUpdate = DateTime.UtcNow;
        const int uiThrottleMs = 20;

        try
        {
            var sessionHandle = _sessionManager.CurrentHandle;
            await foreach (var update in _svc.Chat.ChatStreamingAsync(query, sessionHandle, _cts.Token))
            {
                var token = update.Text ?? "";
                Interlocked.Increment(ref _tokens);

                // Check tool result renderer registry first
                var rendered = _toolRenderers.Render(token);
                if (rendered != null)
                {
                    statusDots.Text = "⚡";
                    responseBuf.Append($" {Truncate(token, 80)}");
                    Dispatcher.UIThread.Post(() =>
                    {
                        toolPanel.IsVisible = true;
                        toolPanel.Children.Add(rendered);
                    });
                    continue;
                }

                // Thinking tags
                if (token.StartsWith("<thinking>"))
                {
                    inThinking = true;
                    thinkBuf.Append(token.AsSpan("<thinking>".Length));
                }
                else if (token.EndsWith("</thinking>"))
                {
                    thinkBuf.Append(token.AsSpan(0, token.Length - "</thinking>".Length));
                    inThinking = false;
                    thinkPanel.IsVisible = true;
                    thinkText.Text = thinkBuf.ToString();
                }
                else if (inThinking)
                {
                    thinkBuf.Append(token);
                    thinkText.Text = thinkBuf.ToString();
                }
                else
                {
                    if (dotTimer.IsEnabled)
                    {
                        dotTimer.Stop();
                        Dispatcher.UIThread.Post(() => aiContent.Children.Remove(statusDots));
                    }

                    if (!firstTokenReceived)
                    {
                        firstTokenReceived = true;
                        taskBanner = new Border
                        {
                            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
                            CornerRadius = LtaiTheme.Radius.Md,
                            Padding = new(6, 3),
                            Margin = new(0, 0, 0, 4),
                            Child = new TextBlock
                            {
                                Text = "⚡ Processing...",
                                Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
                                FontSize = 11
                            }
                        };
                        Dispatcher.UIThread.Post(() => aiContent.Children.Insert(0, taskBanner));
                    }
                    responseBuf.Append(token);
                    var tok = Interlocked.Increment(ref _tokens);
                    if (tok % 8 == 0 && (DateTime.UtcNow - lastUiUpdate).TotalMilliseconds >= uiThrottleMs)
                    {
                        lastUiUpdate = DateTime.UtcNow;
                        var text = responseBuf.ToString();
                        // Code fence awareness: 代码围栏未闭合时不渲染（防止 shiki 报错）
                        if (text != lastRenderedText && !ChatMessageRenderer.HasUnclosedFence(text))
                        {
                            lastRenderedText = text;
                            UpdateResponseText(responsePanel, text);
                        }
                    }
                }

                _scroller.ScrollToEnd();
                if (Volatile.Read(ref _tokens) % 20 == 0)
                    await Task.Yield();
            }

            RenderResponse(responsePanel, responseBuf.ToString());

            if (thinkPanel.IsVisible && thinkBuf.Length > 0 && thinkText.Text?.Length == 0)
                thinkText.Text = thinkBuf.ToString();

            if (taskBanner?.Child is TextBlock tb)
                Dispatcher.UIThread.Post(() => tb.Text = "✅ Complete");

            var aiFullText = responseBuf.ToString();
            var thinkCopy = thinkBuf.Length > 0 ? $"<thinking>\n{thinkBuf}\n</thinking>\n\n" : "";
            var fullCopy = thinkCopy + aiFullText;
            if (fullCopy.Length > 0)
                AddAICopyButton(fullCopy);

            // Plan detection: if response contains a plan, add approve button
            if (aiFullText.Contains("## Plan:") || aiFullText.Contains("approve"))
            {
                var planStatus = LTAI.Agent.Tools.PlanTools.PlanStatus();
                if (!planStatus.Contains("No active plan"))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        var approveBtn = new Button
                        {
                            Content = "✅ Approve Plan",
                            Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
                            Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent),
                            FontWeight = FontWeight.Bold,
                            Margin = new(0, 4, 0, 0),
                            HorizontalAlignment = HorizontalAlignment.Left
                        };
                        approveBtn.Click += (_, _) =>
                        {
                            var result = LTAI.Agent.Tools.PlanTools.ApprovePlan()
                                       + "\n"
                                       + LTAI.Agent.Tools.PlanTools.StartExecution();
                            var statusBlock = new TextBlock
                            {
                                Text = result,
                                Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
                                FontSize = 11,
                                TextWrapping = TextWrapping.Wrap
                            };
                            responsePanel.Children.Add(statusBlock);
                            approveBtn.IsEnabled = false;
                            approveBtn.Content = "✅ Approved";
                        };
                        responsePanel.Children.Add(approveBtn);
                    });
                }
            }

            // Save session after each completed turn
            // (handle was auto-updated by ChatAgent with full MAF session state)
            await _sessionManager.SaveSessionAsync().ConfigureAwait(false);

            // Must dispatch to UI thread after ConfigureAwait(false)
            Dispatcher.UIThread.Post(() => RefreshStats());
        }
        catch (OperationCanceledException)
        {
            responseBuf.Append(" [cancelled]");
            RenderResponse(responsePanel, responseBuf.ToString());
            if (taskBanner?.Child is TextBlock tb)
                Dispatcher.UIThread.Post(() => tb.Text = "⏹ Stopped");
            AddAICopyButton(responseBuf.ToString());
        }
        catch (Exception ex)
        {
            responseBuf.Append($"\n[Error] {ex.Message}");
            RenderResponse(responsePanel, responseBuf.ToString());
            AddAICopyButton(responseBuf.ToString());
        }
        finally
        {
            _cts?.Dispose();
            if (dotTimer.IsEnabled) dotTimer.Stop();
            Dispatcher.UIThread.Post(() => SetSending(false));
        }
    }







    private void ShowStatus()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"回合: {Volatile.Read(ref _turns)}");
        sb.AppendLine($"Token: {Volatile.Read(ref _tokens):N0}");
        sb.AppendLine($"模型: {_svc.Mode}");
        sb.AppendLine($"目录: {Directory.GetCurrentDirectory()}");
        AddSystemBubble(sb.ToString().TrimEnd());
    }

    private void AddSystemBubble(string text)
    {
        var b = new Border
        {
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.AccentSystem),
            BorderThickness = new(1),
            CornerRadius = LtaiTheme.Radius.Md,
            Padding = new(10),
            Margin = new(0, 4)
        };
        var stb = new SelectableTextBlock
        {
            Text = text,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            FontFamily = LtaiTheme.CodeFont,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        };
        b.Child = stb;
        _outputStack.Children.Add(b);
        PruneOutputStack();
        _scroller.ScrollToEnd();
    }

    private async Task GraphInitAsync()
    {
        try
        {
            var cg = _svc.Services.GetService(typeof(LTAI.Agent.Vector.CgGraph)) as LTAI.Agent.Vector.CgGraph;
            var kb = _svc.Services.GetService(typeof(LTAI.Agent.Vector.KbGraph)) as LTAI.Agent.Vector.KbGraph;
            var msgs = new List<string>();
            if (cg != null)
            {
                var codeResult = await cg.BuildAsync().ConfigureAwait(false);
                msgs.Add(codeResult.Replace("\n", " | "));
            }
            if (kb != null)
            {
                var docResult = await kb.BuildDocumentIndexAsync(Directory.GetCurrentDirectory()).ConfigureAwait(false);
                msgs.Add(docResult);
            }
            var msg = msgs.Count > 0 ? string.Join("\n", msgs) : "❌ Graph services not available";
            Dispatcher.UIThread.Post(() => AddSystemBubble($"✅ {msg}"));
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => AddSystemBubble($"❌ Graph init failed: {ex.Message}"));
        }
    }

    private async Task GraphSearchAsync(string query)
    {
        try
        {
            var cg = _svc.Services.GetService(typeof(LTAI.Agent.Vector.CgGraph)) as LTAI.Agent.Vector.CgGraph;
            var kb = _svc.Services.GetService(typeof(LTAI.Agent.Vector.KbGraph)) as LTAI.Agent.Vector.KbGraph;
            var parts = new List<string>();

            if (cg != null)
            {
                var codeResult = await cg.QueryAsync(query, topK: 3).ConfigureAwait(false);
                if (!codeResult.StartsWith("No relevant") && !codeResult.StartsWith("Code graph not built"))
                    parts.Add(codeResult);
            }
            if (kb != null)
            {
                try
                {
                    var kbResults = await kb.QueryAsync(query, topK: 5).ConfigureAwait(false);
                    if (kbResults.Count > 0)
                        parts.Add("## Relevant Knowledge:\n" + string.Join("\n", kbResults.Select(r => "- " + r)));
                }
                catch { }
            }
            var result = parts.Count > 0 ? string.Join("\n\n", parts) : "No results found.";
            Dispatcher.UIThread.Post(() => AddSystemBubble(result));
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => AddSystemBubble($"❌ Search failed: {ex.Message}"));
        }
    }

    private void PruneOutputStack() { }

    private async Task CycleModeAsync()
    {
        try
        {
            var chatAgent = _svc.Services.GetService(typeof(LTAI.Agent.ChatAgent)) as LTAI.Agent.ChatAgent;
            if (chatAgent == null) return;
            var mode = await chatAgent.CycleModeAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                RefreshStats();
                AddSystemBubble($"🔄 模式切换: {mode}");
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                AddSystemBubble($"❌ 模式切换失败: {ex.Message}"));
        }
    }

    private void RefreshStats()
    {
        _footerStats.Children.Clear();
        var dim = new SolidColorBrush(LtaiTheme.TextDim);

        // ── Update Mode / Todo bar ──
        var mode = LTAI.Agent.Tooling.AgentModeObserver.CurrentMode;
        var remaining = LTAI.Agent.Tooling.AgentModeObserver.RemainingTodos;
        var total = LTAI.Agent.Tooling.AgentModeObserver.TotalTodos;
        var icon = LTAI.Agent.Tooling.AgentModeObserver.ModeIcon;

        _modeText.Text = $"{icon} {mode}";
        _modeBar.IsVisible = true;
        _modeText.Foreground = mode.ToLowerInvariant() switch
        {
            "plan" => LtaiTheme.Sbb(LtaiTheme.AccentWarning),
            "execute" or "exec" => LtaiTheme.Sbb(LtaiTheme.AccentDanger),
            _ => LtaiTheme.Sbb(LtaiTheme.TextSecondary),
        };

        if (total > 0)
            _todoText.Text = $"待办: {remaining}/{total}";
        else
            _todoText.Text = "";

        TextBlock Line(string text) => new()
        {
            Text = text,
            Foreground = dim,
            FontSize = 11,
            FontFamily = LtaiTheme.CodeFont,
            TextWrapping = TextWrapping.NoWrap
        };

        var r = LTAI.Core.Configuration.UsageTracker.Requests;

        if (r > 0)
        {
            var l = $"模型: {LTAI.Core.Configuration.UsageTracker.ActiveModel}  " +
                    $"Token: {LTAI.Core.Configuration.UsageTracker.TotalTokens:N0}  " +
                    $"费用: {LTAI.Core.Configuration.UsageTracker.CostDisplay}";
            var tps = LTAI.Core.Configuration.UsageTracker.CurrentTps;
            if (tps.HasValue) l += $"  速率: {tps:F0} t/s";
            l += $"  请求: {r}";
            _footerStats.Children.Add(Line(l));

            var l2 = $"缓存: {LTAI.Core.Configuration.UsageTracker.CacheHitRate:F1}%  " +
                     $"余额: {LTAI.Core.Configuration.UsageTracker.BalanceDisplay}";
            var tc = LTAI.Core.Configuration.UsageTracker.ToolCalls;
            if (tc > 0) l2 += $"  工具: {tc}次";
            var saved = LTAI.Core.Configuration.UsageTracker.CacheSavedDisplay;
            if (saved != "¥0.0000") l2 += $"  节省: {saved}";
            _footerStats.Children.Add(Line(l2));

            var llmTime = LTAI.Core.Configuration.UsageTracker.LlmCallTimeDisplay;
            var toolTime = LTAI.Core.Configuration.UsageTracker.ToolCallTimeDisplay;
            if (!string.IsNullOrEmpty(llmTime) || !string.IsNullOrEmpty(toolTime))
            {
                var timing = new List<string>();
                if (!string.IsNullOrEmpty(llmTime)) timing.Add($"LLM: {llmTime}");
                if (!string.IsNullOrEmpty(toolTime)) timing.Add($"工具: {toolTime}");
                _footerStats.Children.Add(Line(string.Join("  ", timing)));
            }
        }
        else
        {
            _footerStats.Children.Add(Line("等待首次请求...  输入消息开始对话"));
        }
    }

    private static bool TryParseToolResult(string text, out (bool success, string output, string error) result)
    {
        result = default;
        text = text.Trim();
        if (!text.StartsWith('{') || !text.EndsWith('}')) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("success", out var s)) return false;
            var ok = s.GetBoolean();
            var output = ok && root.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
            var err = !ok && root.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
            result = (ok, output, err);
            return true;
        }
        catch { return false; }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";

    private void OnSubagentMessage(int spawnId, string role, string content)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_subSessions.TryGetValue(spawnId, out var subName))
            {
                subName = _sessionManager.CreateChildSession(
                    _sessionManager.CurrentSession ?? "", $"子任务 #{spawnId}");
                _subSessions[spawnId] = subName;
                _subStartTimes[spawnId] = Stopwatch.StartNew();
            }
            // For sub-agent messages, we can't use the MAF session directly since
            // the sub-agent runs in a separate process. Store as simplified handle.
            var subHandle = _sessionManager.LoadSession(subName);
            var currentSession = _sessionManager.CurrentSession;
            if (subHandle == null || currentSession == null)
            {
                if (currentSession != null)
                    _sessionManager.LoadSession(currentSession);
                return;
            }
            var msgs = new List<ChatMessage>(subHandle.Messages)
            {
                new(role == "user" || role == "User" ? ChatRole.User : ChatRole.Assistant, content)
            };
            var json = System.Text.Json.JsonSerializer.Serialize(
                msgs.Select(m => new { Role = m.Role == ChatRole.User ? "user" : "assistant", Content = m.Text ?? "" }));
            subHandle.UpdateFromJson(json);
            _sessionManager.SaveSession(subHandle);
            _sessionManager.LoadSession(currentSession);
        });
    }

    private void OnSubagentComplete(int spawnId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_subSessions.TryGetValue(spawnId, out var subName)) return;

            var elapsed = _subStartTimes.TryGetValue(spawnId, out var sw) ? sw.ElapsedMilliseconds : 0;
            var label = elapsed > 0
                ? $"子任务 #{spawnId} ({elapsed / 1000}.{(elapsed % 1000) / 100}s)"
                : $"子任务 #{spawnId}";
            _sessionManager.SaveMetadata(subName, new { ElapsedMs = elapsed, Label = label });
            AddSystemBubble($"✅ {label} 完成 — 在左侧会话列表中点击查看详情");
        });
    }

    private Action<LTAI.Agent.Tools.QuestionPost>? _questionHandler;

    // ── P17.5 Question Tool Integration ──

    private void SetupQuestionHandler()
    {
        var qs = _svc?.Services?.GetService(typeof(LTAI.Agent.Tools.QuestionService)) as LTAI.Agent.Tools.QuestionService;
        if (qs == null) return;

        _questionHandler = post =>
        {
            _ = Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var owner = this.VisualRoot as Window;
                if (owner == null) return;
                var answers = await LTAI.Desktop.Dialogs.QuestionDialog.ShowAsync(owner, post);
                if (answers.Count > 0)
                    qs.Reply(post.RequestId, answers);
                else
                    qs.Reject(post.RequestId);
            });
        };
        qs.QuestionPosted += _questionHandler;
    }

    // ── IChatRenderer ──

    void IChatRenderer.OnStreamStart() { }
    void IChatRenderer.OnTextDelta(string delta) { }
    void IChatRenderer.OnToolCall(string name, string? arguments) { }
    void IChatRenderer.OnToolResult(string name, string result, bool success) { }
    void IChatRenderer.OnStreamEnd() { }

    void IChatRenderer.RenderMessage(string role, string content,
        IReadOnlyList<ToolCallRecord>? toolCalls, string? reasoning)
    {
        var label = role == "user" ? "[You]" : "[LTAI]";
        var accent = role == "user" ? LtaiTheme.ChatUser : LtaiTheme.ChatAI;
        var border = LtaiTheme.Border;
        Dispatcher.UIThread.Post(() => AddBubble(label, content, accent, border));
    }

    void IChatRenderer.UpdateStatus(string text) { }

    void IChatRenderer.UpdateProgress(string frame, string text, string? elapsed) { }

    ToolResultInfo IChatRenderer.TryParseToolResult(string text)
    {
        var rendered = _toolRenderers.Render(text, null);
        if (rendered != null)
            return new ToolResultInfo(true, true, text, "");
        return new ToolResultInfo(false, false, "", "");
    }

    ConfirmRequest? IChatRenderer.TryParseConfirmRequest(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Contains("⚠️") && text.Contains("确认"))
        {
            var firstLine = text.Split('\n')[0].Trim();
            return new ConfirmRequest("安全确认", firstLine.Replace("⚠️", "").Trim(), "详情见完整内容");
        }
        return null;
    }

    async Task<string?> IChatRenderer.PromptUserAsync(string prompt, bool isSecret)
    {
        var tcs = new TaskCompletionSource<string?>();
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var owner = this.VisualRoot as Window;
            if (owner == null) { tcs.TrySetResult(null); return; }
            var answer = await LTAI.Desktop.Dialogs.QuestionDialog.ShowAsync(owner,
                new LTAI.Agent.Tools.QuestionPost(Guid.NewGuid(),
                    new[] { new LTAI.Agent.Tools.QuestionPrompt(prompt, prompt, Array.Empty<LTAI.Agent.Tools.QuestionOption>(), false) }));
            tcs.TrySetResult(answer.Count > 0 ? string.Join(", ", answer[0]) : null);
        }).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }

    async Task<ConfirmChoice> IChatRenderer.ShowConfirmAsync(
        string title, string message, string result, string extraInfo)
    {
        var tcs = new TaskCompletionSource<ConfirmChoice>();
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var owner = this.VisualRoot as Window;
            if (owner == null) { tcs.TrySetResult(ConfirmChoice.No); return; }
            var dialog = new LTAI.Desktop.Dialogs.ConfirmDialog(title, message);
            var resultChoice = await dialog.ShowDialog<ConfirmChoice?>(owner);
            tcs.TrySetResult(resultChoice ?? ConfirmChoice.No);
        }).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }

    void IChatRenderer.TrimHistory() { }
    void IChatRenderer.AutoCompact() { }
    Task IChatRenderer.SaveSessionAsync() => _sessionManager.SaveSessionAsync();

    async Task IChatRenderer.ExtractMemoryAsync(string userInput)
    {
        var extractor = _svc.Services.GetService(typeof(LTAI.Agent.Memory.MemoryExtractor))
            as LTAI.Agent.Memory.MemoryExtractor;
        if (extractor != null)
            await extractor.ExtractFromTurnAsync(userInput, ct: CancellationToken.None)
                .ConfigureAwait(false);
    }

    void IChatRenderer.RequestRender()
    {
        Dispatcher.UIThread.Post(() => _scroller.ScrollToEnd());
    }
    void IChatRenderer.InvalidateRender()
    {
        Dispatcher.UIThread.Post(() => _scroller.ScrollToEnd());
    }

    private string _rendererStatus = "";
    string IChatRenderer.CurrentStatus
    {
        get => _rendererStatus;
        set => _rendererStatus = value;
    }
}
