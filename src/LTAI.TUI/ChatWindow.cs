using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using LTAI.Agent;
using LTAI.Agent.Tools;
using LTAI.Core.Session;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Editor;
using Terminal.Gui.Editor.Document;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using LTAI.TUI.Dialogs;

using TgAttribute = Terminal.Gui.Drawing.Attribute;

namespace LTAI.TUI;

/// <summary>
/// Main TUI — opencode-inspired borderless layout with background color shading.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly IApplication _app;
    private readonly ChatAgent _chat;
    private readonly SessionManager _sessionMgr;
    private readonly IServiceProvider _sp;
    private readonly List<string> _modifiedFiles = new();
    private readonly List<string> _inputHistory = new();
    private int _historyIndex = -1;
    private CancellationTokenSource? _streamCts;
    private string _streamBuffer = "";
    private bool _chatStarted;
    private System.Threading.Timer? _statsTimer;
    private long _lastUIUpdate;
    private const int UI_THROTTLE_MS = 50;
    private string _gitBranch = "—";
    private Label? _gitBranchLabel;
    private readonly ILogger<MainWindow> _logger;

    private readonly FrameView _homePanel;
    private readonly View _chatPanel;
    private readonly Markdown _markdown;
    private readonly Editor _inputBar;
    private readonly Editor _chatInputBar;
    private readonly SpinnerView _spinner;
    private readonly Label _toolStatus;
    private readonly View _sidebar;
    private readonly List<string> _conv = new();
    private readonly Label _sidebarTokens;
    private readonly Label _sidebarStatus;
    private readonly Label _sidebarModel;
    private readonly Label _sidebarCost;
    private readonly Label _sidebarCache;
    private readonly Label _sidebarTodos;
    private readonly Label _sidebarFiles;
    private string _modelLabelText;
    private Label? _homeModelLabel;
    private Label? _chatModelLabel;
    private Label? _agentModeLabel;
    private Label? _inputPlaceholder;
    private string _agentMode = "build";
    private bool _isStreaming;
    private Editor ActiveInput => _chatStarted ? _chatInputBar : _inputBar;

    private readonly StringBuilder _markdownCache = new(65536);
    private int _aiMsgCachePos = -1;
    private string _lastTodosRaw = "";
    private long _lastActivity;
    private static readonly Regex s_toolFileRegex = new(
        @"(?:编辑|写入|创建)\s+`?([^\s`]+\.\w+)`?", 
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public MainWindow(IApplication app, ChatAgent chat, SessionManager sessionMgr,
        ILogger<MainWindow> logger, string l1ModelLabel = "未配置模型", IServiceProvider? sp = null)
    {
        _app = app;
        _chat = chat;
        _sessionMgr = sessionMgr;
        _sp = sp!;
        _logger = logger;
        Title = "LTAI";
        Width = Dim.Fill();
        Height = Dim.Fill();
        _modelLabelText = l1ModelLabel;

        // Block Terminal.Gui default Ctrl+C exit
        KeyDown += (_, k) =>
        {
            if (k == Key.C && k.IsCtrl && !k.IsAlt && !k.IsShift)
            {
                // Copy selected text or do nothing — prevent exit
                k.Handled = true;
            }
        };

        // ═══════════════════════════════════
        //  HOME panel
        // ═══════════════════════════════════
        _homePanel = new FrameView
        {
            Id = "home", X = 0, Y = 0,
            Width = Dim.Fill(), Height = Dim.Fill(),
            Title = "",
        };

        var logoLabel = new Label
        {
            X = Pos.Center(), Y = 2,
            Text = "  ██╗     ████████╗ █████╗ ██╗\n" +
                   "  ██║     ╚══██╔══╝██╔══██╗██║\n" +
                   "  ██║        ██║   ███████║██║\n" +
                   "  ██║        ██║   ██╔══██║██║\n" +
                   "  ███████╗   ██║   ██║  ██║██║\n" +
                   "  ╚══════╝   ╚═╝   ╚═╝  ╚═╝╚═╝",
        };
        logoLabel.SetScheme(new Scheme(
            new TgAttribute(Color.Cyan, Color.Black)));
        _homePanel.Add(logoLabel);

        var subtitleLabel = new Label
        {
            X = Pos.Center(), Y = 9,
            Text = "多 Agent 协作系统",
        };
        subtitleLabel.SetScheme(new Scheme(
            new TgAttribute(Color.White, Color.Black)));
        _homePanel.Add(subtitleLabel);

        var borderTop = new Label
        {
            X = Pos.Center(), Y = 12,
            Text = "┌──────────────────────────────────────────┐",
        };
        borderTop.SetScheme(new Scheme(
            new TgAttribute(Color.BrightBlue, Color.Black)));
        _homePanel.Add(borderTop);

        _inputBar = new Editor
        {
            X = Pos.Center(), Y = 13,
            Width = 42, Height = 3,
            Multiline = true,
        };
        _inputBar.KeyDown += OnInputKey;
        _inputBar.ContentChanged += OnContentChanged;
        _homePanel.Add(_inputBar);

        var borderBottom = new Label
        {
            X = Pos.Center(), Y = 16,
            Text = "└──────────────────────────────────────────┘",
        };
        borderBottom.SetScheme(new Scheme(
            new TgAttribute(Color.BrightBlue, Color.Black)));
        _homePanel.Add(borderBottom);

        _homePanel.Add(new Label
        {
            X = Pos.Center(), Y = 18,
            Text = "/model 配置模型  ·  /help 帮助  ·  Ctrl+P 命令  ·  Ctrl+Q 退出",
        });

        _homeModelLabel = new Label
        {
            X = Pos.Center(), Y = 21,
            Text = _modelLabelText,
        };
        _homePanel.Add(_homeModelLabel);

        // ═══════════════════════════════════
        //  CHAT panel — borderless, color-shaded
        // ═══════════════════════════════════
        _chatPanel = new View
        {
            Id = "chat", Visible = false,
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
        };

        // Sidebar — darker gray background
        _sidebarTokens = new Label { X = 1, Y = 1, Text = "消息: 0" };
        _sidebarStatus = new Label { X = 1, Y = 2, Text = "状态: 就绪" };
        _sidebarModel = new Label { X = 1, Y = 3, Text = "Token: 0" };
        _sidebarCost = new Label { X = 1, Y = 4, Text = "费用: ¥0" };
        _sidebarCache = new Label { X = 1, Y = 5, Text = "缓存: 0%" };
        _sidebarTodos = new Label { X = 1, Y = 7, Text = "", Width = Dim.Fill() - 1 };
        _sidebarFiles = new Label { X = 1, Y = 12, Text = "", Width = Dim.Fill() - 1 };

        var cwd = Directory.GetCurrentDirectory();
        var cwdDisplay = cwd.Length > 22 ? "..." + cwd[^19..] : cwd;
        var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        var verStr = version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "dev";

        _sidebar = new View
        {
            X = Pos.AnchorEnd(24), Y = 0,
            Width = 24, Height = Dim.Fill() - 3,
        };
        _sidebar.SetScheme(new Scheme(
            new TgAttribute(Color.DarkGray, Color.Black)));
        _sidebar.Add(
            new Label { X = 1, Y = 0, Text = "📊 统计" },
            _sidebarTokens,
            _sidebarStatus,
            _sidebarModel,
            _sidebarCost,
            _sidebarCache,
            _sidebarTodos,
            _sidebarFiles,
            _gitBranchLabel = new Label { X = 1, Y = Pos.AnchorEnd(3), Text = $" {_gitBranch}", Width = Dim.Fill() - 1 },
            new Label { X = 1, Y = Pos.AnchorEnd(2), Text = $" {cwdDisplay}", Width = Dim.Fill() - 1 },
            new Label { X = 1, Y = Pos.AnchorEnd(1), Text = $" {verStr}", Width = Dim.Fill() - 1 }
        );

        // Input area — 4 rows: editor(2) + hints(1) + model+spinner(1)
        var inputAreaView = new View
        {
            X = 0, Y = Pos.AnchorEnd(4),
            Width = Dim.Fill(), Height = 4,
            CanFocus = true,
        };
        inputAreaView.SetScheme(new Scheme(
            new TgAttribute(Color.DarkGray, Color.Black)));

        _chatInputBar = new Editor
        {
            X = 1, Y = 0,
            Width = Dim.Fill() - 2, Height = 2,
            Multiline = true,
            CanFocus = true,
        };
        _chatInputBar.SetScheme(new Scheme(
            new TgAttribute(Color.White, Color.Black)));
        _chatInputBar.KeyDown += OnInputKey;
        _chatInputBar.ContentChanged += OnContentChanged;
        inputAreaView.Add(_chatInputBar);

        // Placeholder text — disappears on input
        _inputPlaceholder = new Label { X = 1, Y = 0, Text = "输入消息...", Width = Dim.Fill(), CanFocus = false };
        _inputPlaceholder.SetScheme(new Scheme(
            new TgAttribute(Color.DarkGray, Color.Black)));
        inputAreaView.Add(_inputPlaceholder);

        var inputHint = new Label { X = 1, Y = 2, Text = "Shift+Enter 换行  ·  Ctrl+P 命令  ·  Ctrl+C 复制  ·  Tab 切换模式", Width = Dim.Fill() };
        inputHint.SetScheme(new Scheme(
            new TgAttribute(Color.DarkGray, Color.Black)));
        inputAreaView.Add(inputHint);

        // Bottom row: spinner + model label + mode
        _spinner = new SpinnerView
        {
            Style = new SpinnerStyle.Dots9(),
            X = 0, Y = 3,
            Width = 8, Height = 1, Visible = false,
        };
        _spinner.SetScheme(new Scheme(
            new TgAttribute(Color.BrightCyan, Color.Black)));
        inputAreaView.Add(_spinner);

        _toolStatus = new Label
        {
            X = 9, Y = 3,
            Width = Dim.Fill() - 16, Text = "", Visible = false,
        };
        _toolStatus.SetScheme(new Scheme(
            new TgAttribute(Color.Yellow, Color.Black)));
        inputAreaView.Add(_toolStatus);

        var chatModelLabel = new Label { X = 9, Y = 3, Text = _modelLabelText, Width = Dim.Fill() - 16 };
        chatModelLabel.SetScheme(new Scheme(
            new TgAttribute(Color.DarkGray, Color.Black)));
        inputAreaView.Add(chatModelLabel);
        _chatModelLabel = chatModelLabel;

        _agentModeLabel = new Label { X = Pos.AnchorEnd(7), Y = 3, Text = "[build]", Width = 8 };
        _agentModeLabel.SetScheme(new Scheme(
            new TgAttribute(Color.BrightCyan, Color.Black)));
        inputAreaView.Add(_agentModeLabel);

        // Messages — leave 4 rows for input area
        _markdown = new Markdown
        {
            X = 0, Y = 0,
            Width = Dim.Fill() - 24,
            Height = Dim.Fill() - 4,
            CanFocus = true,
            ShowCopyButtons = true,
            SyntaxHighlighter = new TextMateSyntaxHighlighter(TextMateSharp.Grammars.ThemeName.DarkPlus),
        };

        _chatPanel.Add(_markdown, _sidebar, inputAreaView);

        Add(_homePanel, _chatPanel);

        _inputBar.SetFocus();

        // Start background stats timer
        _statsTimer = new System.Threading.Timer(_ =>
        {
            if (_chatStarted && (Stopwatch.GetTimestamp() - Volatile.Read(ref _lastActivity)) * 1000L / Stopwatch.Frequency < 30000)
                _app.Invoke(() => RefreshStats());
        }, null, 2000, 2000);

        RestoreSession();

        _ = FetchGitBranchAsync();
    }

    // ═══════════════════════════════════
    //  Input History
    // ═══════════════════════════════════

    private void NavigateHistory(int direction)
    {
        if (_inputHistory.Count == 0) return;
        _historyIndex = Math.Clamp(_historyIndex + direction, -1, _inputHistory.Count - 1);
        ActiveInput.Text = _historyIndex >= 0 ? _inputHistory[_historyIndex] : "";
        ActiveInput.CaretOffset = ActiveInput.Text.Length;
    }

    // ═══════════════════════════════════
    //  Command Picker
    // ═══════════════════════════════════

    private static readonly (string cmd, string desc)[] _commands = new[]
    {
        ("model",    "配置/查看模型"),
        ("new",      "新建会话"),
        ("sessions", "历史会话"),
        ("search",   "搜索对话历史"),
        ("clear",    "清空对话"),
        ("retry",    "重试上一条"),
        ("status",   "当前状态"),
        ("theme",    "切换 Dark/Light 主题"),
        ("commands", "全部命令列表"),
        ("help",     "帮助"),
        ("exit",     "退出应用"),
    };
    private Dialog? _commandPicker;

    private void ShowCommandPicker()
    {
        _commandPicker?.Dispose();
        _commandPicker = new Dialog
        {
            Title = "命令选择器",
            Width = 36, Height = 12,
            X = Pos.Center(), Y = Pos.Center(),
        };

        var items = _commands.Select(c => $"/{c.cmd,-10} {c.desc}").ToList();
        var list = new ListView
        {
            X = 0, Y = 0,
            Width = Dim.Fill(), Height = Dim.Fill(),
        };
        list.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>(items));

        list.KeyDown += (s, k) =>
        {
            if (k == Key.Enter)
            {
                var idx = list.SelectedItem ?? 0;
                if (idx >= 0 && idx < _commands.Length)
                {
                    var cmd = _commands[idx].cmd;
                    DismissCommandPicker();
                    ExecuteCommand(cmd);
                }
                else
                    DismissCommandPicker();
                k.Handled = true;
            }
            if (k == Key.Esc) { DismissCommandPicker(); k.Handled = true; }
        };

        _commandPicker.Add(list);
        var cancelBtn = new Button { Text = "_取消" };
        cancelBtn.Accepting += (_, _) => DismissCommandPicker();
        _commandPicker.AddButton(cancelBtn);
        _commandPicker.Visible = true;
        Add(_commandPicker);
        list.SetFocus();
    }

    private void DismissCommandPicker()
    {
        _commandPicker?.Dispose();
        _commandPicker = null;
        ActiveInput.SetFocus();
        _app.LayoutAndDraw(true);
    }

    private void OnContentChanged(object? s, DocumentChangeEventArgs e)
    {
        // Hide/show placeholder based on input content
        if (_inputPlaceholder != null)
            _inputPlaceholder.Visible = ActiveInput.Text.Length == 0;

        if (ActiveInput.Text == "/" && _commandPicker == null)
            ShowCommandPicker();
        if (_commandPicker != null && ActiveInput.Text.Length == 0)
            DismissCommandPicker();
    }

    // ═══════════════════════════════════
    //  Input Key Handler
    // ═══════════════════════════════════

    private void OnInputKey(object? s, Key k)
    {
        if (k == Key.Esc && _commandPicker != null)
        {
            DismissCommandPicker();
            k.Handled = true;
            return;
        }

        // Block Ctrl+C exit — use for copy instead
        if (k == Key.C && k.IsCtrl && !k.IsAlt && !k.IsShift)
        {
            CopySelection();
            k.Handled = true;
            return;
        }

        // Input history navigation
        if (k == Key.CursorUp && k.IsCtrl)
        {
            NavigateHistory(1);
            k.Handled = true;
            return;
        }
        if (k == Key.CursorDown && k.IsCtrl)
        {
            NavigateHistory(-1);
            k.Handled = true;
            return;
        }

        // Tab: toggle agent mode (plan ↔ build)
        if (k == Key.Tab && !k.IsCtrl && !k.IsAlt)
        {
            _agentMode = _agentMode == "build" ? "plan" : "build";
            _agentModeLabel!.Text = $"[{_agentMode}]";
            _agentModeLabel.SetScheme(new Scheme(
                new TgAttribute(
                    _agentMode == "plan" ? Color.BrightYellow : Color.BrightCyan,
                    Color.Black)));
            k.Handled = true;
            return;
        }

        // Ctrl+R: search conversation history
        if (k == Key.R && k.IsCtrl && !k.IsAlt && !k.IsShift)
        {
            DismissCommandPicker();
            ShowSearchDialog();
            k.Handled = true;
            return;
        }

        // Ctrl+N: new conversation
        if (k == Key.N && k.IsCtrl && !k.IsAlt && !k.IsShift)
        {
            DismissCommandPicker();
            k.Handled = true;
            ExecuteCommand("new");
            return;
        }

        // Ctrl+L: clear conversation
        if (k == Key.L && k.IsCtrl && !k.IsAlt && !k.IsShift)
        {
            DismissCommandPicker();
            k.Handled = true;
            ExecuteCommand("clear");
            return;
        }

        // Ctrl+P: command picker
        if (k == Key.P && k.IsCtrl && !k.IsAlt && !k.IsShift)
        {
            if (_commandPicker == null)
                ShowCommandPicker();
            k.Handled = true;
            return;
        }

        // Ctrl+T: toggle theme
        if (k == Key.T && k.IsCtrl && !k.IsAlt && !k.IsShift)
        {
            DismissCommandPicker();
            k.Handled = true;
            ExecuteCommand("theme");
            return;
        }

        // Ctrl+Q: exit
        if (k == Key.Q && k.IsCtrl && !k.IsAlt && !k.IsShift)
        {
            k.Handled = true;
            _app.RequestStop();
            return;
        }

        if (k == Key.Backspace)
        {
            var pos = ActiveInput.CaretOffset;
            var txt = ActiveInput.Text;
            if (pos > 0 && txt.Length > 0)
            {
                ActiveInput.Text = txt.Remove(pos - 1, 1);
                ActiveInput.CaretOffset = Math.Max(0, pos - 1);
            }
            k.Handled = true;
            return;
        }

        if (k == Key.Enter && k.IsShift)
        {
            var pos = ActiveInput.CaretOffset;
            var txt = ActiveInput.Text;
            ActiveInput.Text = txt.Insert(pos, "\n");
            ActiveInput.CaretOffset = pos + 1;
            k.Handled = true;
            return;
        }

        if (k != Key.Enter) return;
        k.Handled = true;
        _lastActivity = Stopwatch.GetTimestamp();
        var text = ActiveInput.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        if (text.StartsWith("/"))
        {
            var p = text.TrimStart('/').Split(' ');
            ActiveInput.Text = "";
            ExecuteCommand(p[0].ToLowerInvariant());
            return;
        }

        // Transition home → chat
        if (!_chatStarted)
        {
            _chatStarted = true;
            _homePanel.Visible = false;
            _chatPanel.Visible = true;
            _chatPanel.SetNeedsLayout();

            _chatInputBar.Text = text;
            _chatInputBar.SetFocus();
            AddMsg("You", text);
            _inputHistory.Add(text);
            _historyIndex = -1;
            if (_streamCts != null) CancelStream();
            _streamCts = new CancellationTokenSource();
            _ = StreamAsync(text, _streamCts.Token);
            _chatInputBar.Text = "";
            return;
        }

        if (_isStreaming) return;
        _chatInputBar.Text = "";
        AddMsg("You", text);
        _inputHistory.Add(text);
        _historyIndex = -1;
        if (_streamCts != null) CancelStream();
        _streamCts = new CancellationTokenSource();
        _ = StreamAsync(text, _streamCts.Token);
    }

    // ═══════════════════════════════════
    //  Copy Selection
    // ═══════════════════════════════════

    private void CopySelection()
    {
        try
        {
            var lastAI = _conv.LastOrDefault(m => m.StartsWith("**AI:**"));
            if (lastAI != null)
            {
                var text = lastAI.Replace("**AI:** ", "").Trim();
                _app.Clipboard?.SetClipboardData(text);
            }
        }
        catch
        {
            // non-critical, best-effort
        }
    }

    // ═══════════════════════════════════
    //  Messages & Markdown
    // ═══════════════════════════════════

    private void AddMsg(string role, string md)
    {
        _conv.Add($"**{role}:** {md}");
        if (_markdownCache.Length > 0)
            _markdownCache.Append("\n\n");
        _markdownCache.Append($"**{role}:** {md}");
        if (role == "AI")
            _aiMsgCachePos = -1;
        _markdown.Text = _markdownCache.ToString();
        _sidebarTokens.Text = $"消息: {_conv.Count}";
        RefreshStats();
    }

    private void UpdateMarkdown()
    {
        _markdown.Text = _markdownCache.ToString();
    }

    // ═══════════════════════════════════
    //  Stats & Sidebar
    // ═══════════════════════════════════

    private void RefreshStats()
    {
        _sidebarModel.Text = $"Token: {LTAI.Core.Configuration.UsageTracker.TotalTokens:N0}";
        _sidebarCost.Text = $"费用: {LTAI.Core.Configuration.UsageTracker.CostDisplay}";
        _sidebarCache.Text = $"缓存: {LTAI.Core.Configuration.UsageTracker.CacheHitRate:F0}%";

        // Refresh todo list — only re-parse when raw string changed
        var todos = TaskTools.TodoList();
        if (todos == "No todos.")
            _sidebarTodos.Text = "";
        else if (todos != _lastTodosRaw)
        {
            _lastTodosRaw = todos;
            var todoLines = todos.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.StartsWith("|") && !l.StartsWith("| #") && !l.StartsWith("|---"))
                .Select(l =>
                {
                    var parts = l.Split('|', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3) return "";
                    var icon = parts[1].Trim().StartsWith("✅") ? "✓"
                             : parts[1].Trim().StartsWith("🔄") ? "▸" : "○";
                    var name = parts[2].Trim();
                    return $" {icon} {name}";
                })
                .Where(s => !string.IsNullOrEmpty(s))
                .Take(4)
                .ToList();
            _sidebarTodos.Text = todoLines.Count > 0
                ? "待办\n" + string.Join("\n", todoLines)
                : "";
        }

        // Refresh modified files
        if (_modifiedFiles.Count > 0)
        {
            var fileLines = _modifiedFiles.TakeLast(4)
                .Select(f => $" ✎ {Path.GetFileName(f)}")
                .ToList();
            _sidebarFiles.Text = "文件\n" + string.Join("\n", fileLines);
        }
        else
            _sidebarFiles.Text = "";
    }

    // ═══════════════════════════════════
    //  Streaming
    // ═══════════════════════════════════

    private void CancelStream()
    {
        if (_streamCts != null)
        {
            _streamCts.Cancel();
            _streamCts.Dispose();
            _streamCts = null;
        }
    }

    private async Task StreamAsync(string input, CancellationToken ct)
    {
        var handle = _sessionMgr.CurrentHandle;
        _streamBuffer = "";
        _isStreaming = true;
        _app.Invoke(() =>
        {
            _spinner.Visible = true;
            _spinner.AutoSpin = true;
            _sidebarStatus.Text = "状态: 思考中...";
            RefreshStats();
        });
        _conv.Add("**AI:** ");
        if (_markdownCache.Length > 0)
            _markdownCache.Append("\n\n");
        _aiMsgCachePos = _markdownCache.Length;
        _markdownCache.Append("**AI:** ");
        UpdateMarkdown();
        var tokenCount = 0;
        try
        {
            await foreach (var u in _chat.ChatStreamingAsync(input, handle).WithCancellation(ct))
            {
                if (ct.IsCancellationRequested) break;
                var t = u.Text ?? ""; if (t.Length == 0) continue;
                _streamBuffer += t;
                tokenCount++;

                // Track modified files
                if (t.Contains("正在调用") && (t.Contains("Edit") || t.Contains("Write") || t.Contains("Create")))
                {
                    var fileMatch = s_toolFileRegex.Match(_streamBuffer);
                    if (fileMatch.Success)
                    {
                        var filePath = fileMatch.Groups[1].Value;
                        if (!_modifiedFiles.Contains(filePath))
                            _modifiedFiles.Add(filePath);
                    }
                }

                var isToolMsg = t.Contains("正在调用") || t.Contains("返回:");

                // Throttled UI update
                var now = Stopwatch.GetTimestamp();
                var elapsed = (now - _lastUIUpdate) * 1000.0 / Stopwatch.Frequency;
                if (elapsed >= UI_THROTTLE_MS || tokenCount % 3 == 0)
                {
                    _lastUIUpdate = now;
                    _lastActivity = now;
                    _app.Invoke(() =>
                    {
                        if (isToolMsg)
                        {
                            var trimmed = _streamBuffer.TrimEnd();
                            var lastNewline = trimmed.LastIndexOf('\n');
                            var statusLine = lastNewline >= 0 ? trimmed[(lastNewline + 1)..] : trimmed;
                            _toolStatus.Text = statusLine;
                            _toolStatus.Visible = true;
                            _spinner.AutoSpin = true;
                        }
                        else
                        {
                            _toolStatus.Visible = false;
                        }
                        if (_conv.Count > 0)
                        {
                            // Show blinking cursor during streaming
                            _conv[^1] = $"**AI:** {_streamBuffer}▊";
                            if (_aiMsgCachePos >= 0)
                            {
                                _markdownCache.Length = _aiMsgCachePos;
                                _markdownCache.Append($"**AI:** {_streamBuffer}▊");
                            }
                            _markdown.Text = _markdownCache.ToString();
                            // Auto-scroll to bottom
                            var ch = _markdown.GetContentSize().Height;
                            if (ch > 0)
                                _markdown.Viewport = _markdown.Viewport with { Y = Math.Max(0, ch - _markdown.Viewport.Height) };
                        }
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected cancellation
        }
        catch (Exception ex) { _aiMsgCachePos = -1; _app.Invoke(() => AddMsg("System", $"⚠ {ex.Message}")); }
        finally
        {
            var cancelled = ct.IsCancellationRequested;
            _isStreaming = false;
            _app.Invoke(() =>
            {
                _spinner.Visible = false;
                _spinner.AutoSpin = false;
                _toolStatus.Visible = false;
                // Skip destructive mutations if a new stream already started (this one was cancelled)
                if (_conv.Count > 0 && !cancelled)
                {
                    _conv[^1] = $"**AI:** {_streamBuffer}";
                    if (_aiMsgCachePos >= 0)
                    {
                        _markdownCache.Length = _aiMsgCachePos;
                        _markdownCache.Append($"**AI:** {_streamBuffer}");
                    }
                    _markdown.Text = _markdownCache.ToString();
                }
                _sidebarStatus.Text = cancelled ? "状态: 已取消" : "状态: 就绪";
                RefreshStats();
            });
            if (!ct.IsCancellationRequested) await _sessionMgr.SaveSessionAsync();
        }
    }

    // ═══════════════════════════════════
    //  Commands
    // ═══════════════════════════════════

    private void ExecuteCommand(string cmd)
    {
        switch (cmd)
        {
            case "new":
                CancelStream();
                _conv.Clear();
                _markdownCache.Clear();
                _aiMsgCachePos = -1;
                UpdateMarkdown();
                _sidebarTokens.Text = "消息: 0";
                return;
            case "clear":
                CancelStream();
                _conv.Clear();
                _markdownCache.Clear();
                _aiMsgCachePos = -1;
                UpdateMarkdown();
                _sidebarTokens.Text = "消息: 0";
                return;
            case "sessions":
                ShowSessionPicker();
                return;
            case "search":
                ShowSearchDialog();
                return;
            case "retry":
                AddMsg("System", "重发暂未实现");
                return;
            case "model":
                HandleModelCommand();
                return;
            case "status":
                AddMsg("System", $"**状态**\n- 消息数: {_conv.Count}\n- 模型: {_modelLabelText}\n- 会话: {_sessionMgr.CurrentHandle?.Name ?? "—"}\n- 工具调用: {LTAI.Core.Configuration.UsageTracker.ToolCalls}");
                return;
            case "commands":
                AddMsg("System", "**可用命令**\n\n`/model` 配置模型\n`/new` 新建会话\n`/sessions` 历史会话\n`/clear` 清空对话\n`/theme` 切换主题\n`/retry` 重试\n`/status` 状态\n`/help` 帮助\n`/exit` 退出");
                return;
            case "theme":
                try
                {
                    var themeNames = Terminal.Gui.Configuration.ThemeManager.GetThemeNames().ToList();
                    var curTheme = Terminal.Gui.Configuration.ThemeManager.Theme;
                    var nextTheme = themeNames.FirstOrDefault(n => n != curTheme) ?? curTheme;
                    Terminal.Gui.Configuration.ThemeManager.Theme = nextTheme;
                    if (!Terminal.Gui.Configuration.ConfigurationManager.IsEnabled)
                        Terminal.Gui.Configuration.ConfigurationManager.Enable(Terminal.Gui.Configuration.ConfigLocations.None);
                    Terminal.Gui.Configuration.ConfigurationManager.Apply();
                    AddMsg("System", $"主题: {curTheme} → {nextTheme}");
                }
                catch (Exception ex) { AddMsg("System", $"主题切换失败: {ex.Message}"); }
                return;
            case "help":
                AddMsg("System", "输入 `/commands` 查看全部命令\n快捷键: `Ctrl+N` 新建 · `Ctrl+L` 清空 · `Ctrl+P` 命令\n`Ctrl+R` 搜索 · `Ctrl+↑/↓` 翻阅历史 · `Shift+Enter` 换行");
                return;
            case "exit":
                _app.RequestStop();
                return;
            default:
                AddMsg("System", $"未知 `/{cmd}`");
                return;
        }
    }

    private void ShowSearchDialog()
    {
        var dlg = new Dialog
        {
            Title = "历史搜索",
            Width = 60, Height = 18,
            X = Pos.Center(), Y = Pos.Center(),
        };

        var searchInput = new Editor
        {
            X = 1, Y = 0, Width = Dim.Fill() - 2, Height = 1,
        };
        dlg.Add(searchInput);

        var resultList = new ListView
        {
            X = 1, Y = 2, Width = Dim.Fill() - 2, Height = Dim.Fill() - 4,
        };
        dlg.Add(resultList);

        var resultItems = new List<string>();
        void DoSearch()
        {
            var q = (searchInput.Text ?? "").Trim().ToLowerInvariant();
            resultItems.Clear();
            if (q.Length > 0)
            {
                for (int i = 0; i < _conv.Count; i++)
                {
                    var lower = _conv[i].ToLowerInvariant();
                    if (lower.Contains(q))
                        resultItems.Add($"#{i + 1} {_conv[i][..Math.Min(_conv[i].Length, 80)]}");
                }
            }
            resultList.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>(resultItems));
        };

        searchInput.ContentChanged += (_, _) => DoSearch();
        searchInput.KeyDown += (s, k) =>
        {
            if (k == Key.Esc) { dlg.RequestStop(); k.Handled = true; }
        };
        resultList.KeyDown += (s, k) =>
        {
            if (k == Key.Esc) { dlg.RequestStop(); k.Handled = true; }
        };

        var closeBtn = new Button { Text = "_关闭" };
        closeBtn.Accepting += (_, _) => dlg.RequestStop();
        dlg.AddButton(closeBtn);
        dlg.Visible = true;
        Add(dlg);
        searchInput.SetFocus();
    }

    private void ShowSessionPicker()
    {
        var sessions = _sessionMgr.ListSessions();
        if (sessions.Length == 0)
        {
            AddMsg("System", "暂无历史会话");
            return;
        }

        var dlg = new Dialog
        {
            Title = "历史会话",
            Width = 50, Height = Math.Min(sessions.Length + 4, 15),
            X = Pos.Center(), Y = Pos.Center(),
        };

        var list = new ListView
        {
            X = 0, Y = 0,
            Width = Dim.Fill(), Height = Dim.Fill(),
        };
        var sessionNames = sessions.Reverse().ToArray();
        list.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>(
            sessionNames.Select(s => s.Name).ToList()));

        list.KeyDown += (s, k) =>
        {
            if (k == Key.Enter)
            {
                var idx = list.SelectedItem ?? 0;
                if (idx >= 0 && idx < sessionNames.Length)
                {
                    LoadSession(sessionNames[idx].Name);
                    dlg.RequestStop();
                }
                k.Handled = true;
            }
            if (k == Key.Esc) { dlg.RequestStop(); k.Handled = true; }
        };

        dlg.Add(list);
        var cancelBtn = new Button { Text = "_取消" };
        cancelBtn.Accepting += (_, _) => dlg.RequestStop();
        dlg.AddButton(cancelBtn);
        dlg.Visible = true;
        Add(dlg);
        list.SetFocus();
    }

    private void LoadSession(string sessionName)
    {
        try
        {
            var h = _sessionMgr.LoadSession(sessionName);
            if (h?.Messages is { Count: > 0 } msgs)
            {
                _conv.Clear();
                _markdownCache.Clear();
                _aiMsgCachePos = -1;
                foreach (var m in msgs)
                {
                    var line = $"**{(m.Role == ChatRole.User ? "You" : "AI")}:** {m.Text ?? ""}";
                    _conv.Add(line);
                    if (_markdownCache.Length > 0) _markdownCache.Append("\n\n");
                    _markdownCache.Append(line);
                }
                _chatStarted = true;
                _homePanel.Visible = false;
                _chatPanel.Visible = true;
                _chatPanel.SetNeedsLayout();
                _chatInputBar.SetFocus();
                UpdateMarkdown();
                _sidebarTokens.Text = $"消息: {_conv.Count}";
                AddMsg("System", $"已加载会话: {sessionName}");
            }
        }
        catch (Exception ex) { AddMsg("System", $"加载会话失败: {ex.Message}"); }
    }

    private void HandleModelCommand()
    {
        try
        {
            ModelConfigDialog.Run(_app, "L1");
            UpdateModelLabel();
            _app.LayoutAndDraw(true);
        }
        catch (Exception ex) { AddMsg("System", $"⚠ 模型配置错误: {ex.Message}"); }
    }

    private void UpdateModelLabel()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        try
        {
            if (!File.Exists(path)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("LTAI", out var ltai)) return;
            if (!ltai.TryGetProperty("AI", out var ai)) return;
            if (!ai.TryGetProperty("L1", out var l1)) return;
            var provider = l1.TryGetProperty("Provider", out var p) ? p.GetString() ?? "" : "";
            var model = l1.TryGetProperty("Model", out var m) ? m.GetString() ?? "" : "";
            _modelLabelText = !string.IsNullOrEmpty(provider) ? $"L1: {provider} / {model}" : "未配置模型 (使用 /model 配置)";
            if (_homeModelLabel != null) _homeModelLabel.Text = _modelLabelText;
        }
        catch
        {
            // non-critical, best-effort
        }
    }

    private void RestoreSession()
    {
        try
        {
            var sessions = _sessionMgr.ListSessions();
            if (sessions.Length == 0) return;
            var h = _sessionMgr.LoadSession(sessions[^1].Name);
            if (h?.Messages is { Count: > 0 } msgs)
            {
                _chatStarted = true;
                _homePanel.Visible = false;
                _chatPanel.Visible = true;
                _chatPanel.SetNeedsLayout();
                _chatInputBar.SetFocus();
                foreach (var m in msgs)
                {
                    var line = $"**{(m.Role == ChatRole.User ? "You" : "AI")}:** {m.Text ?? ""}";
                    _conv.Add(line);
                    if (_markdownCache.Length > 0) _markdownCache.Append("\n\n");
                    _markdownCache.Append(line);
                }                UpdateMarkdown();
                _sidebarTokens.Text = $"消息: {_conv.Count}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RestoreSession failed");
        }
    }

    private async Task FetchGitBranchAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --abbrev-ref HEAD")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = Directory.GetCurrentDirectory(),
            };
            using var p = Process.Start(psi);
            if (p == null) return;
            using var cts = new CancellationTokenSource(2000);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            var branch = p.ExitCode == 0 ? (await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false)).Trim() : "";
            if (!string.IsNullOrEmpty(branch))
            {
                _gitBranch = branch;
                _app.Invoke(() =>
                {
                    if (_gitBranchLabel != null)
                        _gitBranchLabel.Text = $" {_gitBranch}";
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Git branch fetch failed");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _statsTimer?.Dispose();
            _statsTimer = null;
            _streamCts?.Cancel();
            _streamCts?.Dispose();
            _streamCts = null;
        }
        base.Dispose(disposing);
    }
}
