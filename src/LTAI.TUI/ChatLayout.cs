using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using LTAI.Agent;
using LTAI.Core.Configuration;
using LTAI.Core.Session;
using LTAI.Agent.Tools;
using LTAI.TUI.Rendering;
using LTAI.TUI.Services;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LTAI.TUI;

using static ThemeService;

public sealed partial class ChatLayout : IDisposable, IStreamerHost
{
    private readonly ChatAgent _chat;
    private readonly Rendering.ChatRenderer _renderer;
    internal readonly List<(string role, IRenderable? rendered, string rawContent, string? reasoning)> _history = new();
    internal readonly object _historyLock = new();
    internal readonly Stack<List<(string role, IRenderable? rendered, string rawContent, string? reasoning)>> _undoStack = new();
    internal const int MaxUndoStack = 20;
    private readonly Layout _layout;
    private readonly Layout _messagesLayout;
    private readonly Layout _footerLayout;
    private readonly QuestionService _questionService;
    private readonly SessionManager _sessions;
    private readonly TextPadView _textPadView;
    private readonly SessionsCommandHandler _sessionHandler;
    private volatile QuestionPost? _pendingQuestion;

    internal readonly System.Threading.Channels.Channel<string> _messageQueue =
        System.Threading.Channels.Channel.CreateBounded<string>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    /// <summary>Send a message as if the user typed it (used by TextPadView AI fix, etc.).</summary>
    public void EnqueueUserMessage(string message) => _messageQueue.Writer.TryWrite(message);

    /// <summary>Enqueue a restored message from a previous session (not user-initiated).</summary>
    public void EnqueueRestoredMessage(string role, string content)
    {
        lock (_historyLock)
        {
            _history.Add((role, null, content, null));
        }
    }
    internal volatile bool _processing;
    internal CancellationTokenSource? _responseCts;
    private CancellationTokenSource? _renderCts;
    internal volatile char _quickNav;
    private static string? _startupMessage;
    private readonly LTAI.Agent.Memory.MemoryExtractor? _memoryExtractor;

    // ── 多行输入缓冲区 ──
    internal readonly List<string> _inputLines = new() { "" };
    internal int _cursorLine = 0;
    internal int _cursorCol = 0;
    internal const int MaxInputLines = 5;

    // ── 输入历史（Ctrl+↑↓） ──
    internal readonly List<string> _inputHistory = new();
    internal int _historyIndex = -1;

    // ── 输出滚动 ──
    internal int _scrollOffset = 0;
    private const int ScrollStep = 3;

    // ── 当前 Turn 的工具调用（临时，不进 history） ──
    internal readonly List<(string name, string args, string result)> _toolCalls = new();

    // ── 可折叠推理过程的展开状态 ──
    internal readonly HashSet<int> _expandedMessages = new();

    /// <summary>Set a persistent message shown in the initial empty state (before any conversation).</summary>
    public static void SetStartupMessage(string message) => _startupMessage = message;
    private static string? ConsumeStartupMessage() { var m = _startupMessage; _startupMessage = null; return m; }

    // 选择器状态（由输入任务管理，主线程只读）
    internal volatile bool _pickerActive;
    internal volatile bool _viewPickerActive;
    internal int _viewPickerSelected;
    internal readonly object _pickerLock = new();
    internal string _pickerFilter = "";
    internal List<SlashCommands.SuggestionItem> _pickerItems = new();
    internal int _pickerSelectedIdx;
    private LiveDisplayContext? _liveCtx;
    public TuiView? LastRequestedView { get; private set; }
    public static string CurrentViewName { get; set; } = "聊天";
    public string? CurrentFileForContext => _textPadView.CurrentFileForContext;
    private int _subagentProgress;
    internal string? _statusMessage;
    internal string? _pendingChatRequest;
    internal string? _pendingSearchTerm;
    internal int _renderVersion;
    internal int _historyVersion;
    
    internal static TaskCompletionSource<ConfirmChoice>? PendingConfirmTcs;
    internal static string? PendingConfirmDetails;
    internal static string? PendingConfirmTitle;
    internal static string? PendingConfirmMessage;
    internal static string? PendingConfirmExtra;
    public static string? EditMode { get; set; }
    public static Func<bool>? TryUndoCallback { get; set; }
    private const int MaxHistory = 200;
    private const int RefreshIntervalMs = 33;
    private DateTime _lastRefresh = DateTime.MinValue;
    private DateTime _lastCmdTime = DateTime.MinValue;
    private readonly int _maxVisibleMessages;
    private readonly Action<int> _onSubagentComplete;
    private readonly Action<int, string, string> _onSubagentMessage;

    void IStreamerHost.ThrottledRefresh() => ThrottledRefresh();
    private void ThrottledRefresh()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastRefresh).TotalMilliseconds >= RefreshIntervalMs && _liveCtx != null)
        {
            _liveCtx.Refresh();
            _lastRefresh = now;
        }
    }

    internal void SnapshotForUndo()
    {
        lock (_historyLock)
        {
            var snapshot = _history.Select(h => (h.role, h.rendered, h.rawContent, h.reasoning)).ToList();
            _undoStack.Push(snapshot);
            while (_undoStack.Count > MaxUndoStack) { var _ = _undoStack.Pop(); }
        }
    }

    internal bool TryUndo()
    {
        lock (_historyLock)
        {
            if (_undoStack.Count == 0) return false;
            var prev = _undoStack.Pop();
            _history.Clear();
            _history.AddRange(prev);
            _toolCalls.Clear();
            InvalidateRendered();
            return true;
        }
    }

    void IStreamerHost.TrimHistory() => TrimHistory();
    internal void TrimHistory()
    {
        if (_history.Count <= MaxHistory) return;
        var removeCount = _history.Count - MaxHistory;
        _history.RemoveRange(0, removeCount);
    }

    void IStreamerHost.InvalidateRendered() => InvalidateRendered();
    internal void InvalidateRendered()
    {
        Interlocked.Increment(ref _renderVersion);
    }

    public ChatLayout(ChatAgent chat, Rendering.ChatRenderer renderer, QuestionService? questionService = null,
        SessionManager? sessions = null, TextPadView? textPadView = null,
        LTAI.Agent.Memory.PalaceStore? palaceStore = null)
    {
        _chat = chat;
        _renderer = renderer;
        _sessions = sessions ?? new SessionManager();
        TryUndoCallback = TryUndo;
        LoadInputHistory();
        _textPadView = textPadView ?? new TextPadView(Directory.GetCurrentDirectory());
        _questionService = questionService ?? new QuestionService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<QuestionService>.Instance);
        _questionService.QuestionPosted += post => _pendingQuestion = post;

        // 自动计算可见消息数，确保输入区始终固定
        // 每条消息 ≈ 4 行，Footer 4 行（输入区3+状态条1）
        var termHeight = Math.Max(24, SafeWindowHeight);
        _maxVisibleMessages = Math.Max(3, (termHeight - 6) / 4);

        _layout = new Layout()
            .SplitRows(
                new Layout("Messages"),
                new Layout("Footer").Size(4));
        _messagesLayout = _layout["Messages"];
        _footerLayout = _layout["Footer"];

        _messagesLayout.Update(
            new Panel(
                $"[bold {WarningTag}]💬 欢迎使用 LTAI[/]\n\n" +
                $"[{MutedTag}]可用命令:[/]\n" +
                $"  [{PrimaryTag}]/new[/]     — 新建会话\n" +
                $"  [{PrimaryTag}]/help[/]    — 显示帮助\n" +
                $"  [{PrimaryTag}]/exit[/]    — 退出\n" +
                $"  [{PrimaryTag}]/model[/]   — 管理模型\n" +
                $"  [{PrimaryTag}]/config[/]  — 配置 LLM\n\n" +
                $"[{MutedTag}]快捷键:[/]\n" +
                $"  [{PrimaryTag}]1-5[/]       — 切换视图\n" +
                $"  [{PrimaryTag}]↑↓[/]       — 历史消息\n" +
                $"  [{PrimaryTag}]/[/]         — 打开命令选择器\n\n" +
                $"[{MutedTag}]直接输入消息开始对话，或输入 [{WarningTag}]/[/] 浏览全部命令[/]")
                .Border(BoxBorder.Rounded)
                .Header(new PanelHeader($"[bold {WarningTag}]💬 LTAI[/]"))
                .Expand());

        _footerLayout.Update(
            new Panel($"[{MutedTag}]等待首次请求...  输入消息开始对话[/]")
                .Border(BoxBorder.None).Expand());

        _onSubagentComplete = (id) => _subagentProgress = -1;
        _onSubagentMessage = (id, role, content) =>
        {
            Interlocked.Increment(ref _subagentProgress);
        };
        LTAI.Agent.Tools.SubagentTools.OnSubagentComplete += _onSubagentComplete;
        LTAI.Agent.Tools.SubagentTools.OnSubagentMessage += _onSubagentMessage;
        _memoryExtractor = palaceStore != null
            ? new LTAI.Agent.Memory.MemoryExtractor(palaceStore, null)
            : null;
        _sessionHandler = new SessionsCommandHandler(_sessions);

        NotificationService.OnNotification += entry =>
        {
            var color = entry.Level switch
            {
                NotificationLevel.Error => ThemeService.ErrorTag,
                NotificationLevel.Warning => ThemeService.WarningTag,
                _ => ThemeService.AccentTag
            };
            _statusMessage = $"[{color}]{Markup.Escape(entry.Message)}[/]";
        };
    }

    public void Dispose()
    {
        LTAI.Agent.Tools.SubagentTools.OnSubagentComplete -= _onSubagentComplete;
        LTAI.Agent.Tools.SubagentTools.OnSubagentMessage -= _onSubagentMessage;
        _responseCts?.Cancel();
        _responseCts?.Dispose();
        if (_renderCts != null) { _renderCts.Cancel(); _renderCts.Dispose(); _renderCts = null; }
    }

    public async Task<TuiView?> RenderAsync()
    {
        LastRequestedView = TuiView.Chat;
        await SetupAsync().ConfigureAwait(false);
        return await RunMainLoopAsync().ConfigureAwait(false);
    }

    private async Task SetupAsync()
    {
        try
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("[bold deepskyblue1]🔌 初始化 LTAI...</bold>", async ctx =>
                {
                    ctx.Status = "[bold]预热 LLM 连接...[/]";
                    var warmupTask = _chat.WarmUpAsync();
                    // WarmUp failure is handled by the following WaitAsync timeout + catch

                    try { await warmupTask.WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ChatLayout] WarmUp timeout: {ex.Message}"); }

                    ctx.Status = "[green]✓ 初始化完成[/]";
                    await Task.Delay(200);
                }).ConfigureAwait(false);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ChatLayout] SetupAsync failed: {ex.Message}"); }
    }

    private async Task<TuiView?> RunMainLoopAsync()
    {
        TuiView? result = TuiView.Chat;
        try
        {
            await AnsiConsole.Live(_layout)
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .Cropping(VerticalOverflowCropping.Top)
            .StartAsync(async ctx =>
            {
                _liveCtx = ctx;
                var showWatermark = true;
                _processing = false;

                // P17.5: background task that reads keys independently from
                // message processing. User can type the next message while the
                // LLM is still responding to the previous one.
                _renderCts?.Dispose();
                _renderCts = new CancellationTokenSource();
                var cts = _renderCts;

                var inputTask = Task.Run(async () =>
                {
                    Input.MouseTracker.Enable();
                    var keyDispatcher = new Input.KeyDispatcher(this);
                    while (!cts.Token.IsCancellationRequested)
                    {
                        var evt = Input.MouseTracker.ReadNext(cts.Token);
                        if (evt.KeyInfo.HasValue)
                        {
                            var keepGoing = await keyDispatcher.HandleKeyAsync(evt.KeyInfo.Value, cts.Token).ConfigureAwait(false);
                            if (!keepGoing) { cts.Cancel(); return; }
                        }
                        else if (evt.ScrollDelta != 0)
                        {
                            // Shift+↑/↓ gesture for history scroll
                            var oldOffset = _scrollOffset;
                            _scrollOffset = Math.Clamp(_scrollOffset - evt.ScrollDelta, 0, Math.Max(0, _history.Count - 1));
                            if (_scrollOffset != oldOffset)
                                ThrottledRefresh();
                        }
                        else if (evt.ClickPosition.HasValue)
                        {
                            var (row, col) = evt.ClickPosition.Value;
                            var termHeight = SafeWindowHeight;
                            var msgAreaEnd = termHeight - 4;

                            // Click in the rightmost 5 columns = copy latest code block
                            var termWidth = 120;
                            try { termWidth = Console.WindowWidth; } catch { System.Diagnostics.Debug.WriteLine("[ChatLayout] WindowWidth query failed"); }
                            if (col >= termWidth - 5 && row < msgAreaEnd)
                            {
                                CodeBlockBuffer.TryCopyLatestToClipboard();
                            }
                            else if (row < msgAreaEnd / 3 && _scrollOffset < _history.Count)
                            {
                                _scrollOffset = Math.Min(_scrollOffset + 5, Math.Max(0, _history.Count - 1));
                            }
                            else if (row > msgAreaEnd * 2 / 3 && _scrollOffset > 0)
                            {
                                _scrollOffset = Math.Max(0, _scrollOffset - 5);
                            }
                            else
                            {
                                _scrollOffset = 0;
                            }
                            ThrottledRefresh();
                        }
                    }
                    Input.MouseTracker.Disable();
                }, cts.Token);

                // Main rendering + processing loop: runs at ~30fps, processes
                // queued messages one at a time while keeping the UI responsive.
                while (!cts.Token.IsCancellationRequested)
                {
                    // Refresh UI
                    if (_viewPickerActive)
                    {
                        lock (_layout)
                        {
                            _messagesLayout.Update(BuildViewSwitcherOverlay());
                            UpdateFooter("", "", showWatermark);
                            ctx.Refresh();
                        }
                    }
                    else if (_pickerActive)
                    {
                        string filter;
                        List<SlashCommands.SuggestionItem> items;
                        int selIdx;
                        lock (_pickerLock)
                        {
                            filter = _pickerFilter;
                            items = _pickerItems;
                            selIdx = _pickerSelectedIdx;
                        }
                        lock (_layout)
                        {
                            // 命令面板 overlay: 在 Messages 区中央显示浮动列表
                            _messagesLayout.Update(BuildPickerOverlay(filter, items, selIdx));
                            UpdateFooter("", "", showWatermark);
                            ctx.Refresh();
                        }
                    }
                    else
                    {
                        // 非级联菜单: 有 cmd 消息且超过 30s 或用户已开始对话 → 清理
                        if (!LTAI.TUI.SlashCommands.InCascadeMenu && _history.Any(x => x.role == "cmd"))
                        {
                            var hasUserMsg = _history.Any(x => x.role == "user");
                            var expired = (DateTime.UtcNow - _lastCmdTime).TotalSeconds > 30;
                            if (hasUserMsg || expired)
                            {
                                _history.RemoveAll(x => x.role == "cmd");
                            }
                        }
                        lock (_layout) { UpdateMessages(""); UpdateFooter("", "", showWatermark); ctx.Refresh(); }
                    }
                    showWatermark = false;

                    // Check quick-navigation from input thread
                    lock (_layout)
                    {
                        if (_quickNav != default)
                        {
                            var nextView = _quickNav switch
                            {
                                '1' => TuiView.Dashboard, '3' => TuiView.LLMConfig,
                                '4' => TuiView.TextPad, '5' => TuiView.Skills,
                                '6' => TuiView.Sessions, '7' => TuiView.Jobs,
                                '8' => TuiView.MemoryBrowser, '9' => TuiView.Workflows,
                                '0' => TuiView.GraphBrowser, _ => TuiView.Chat
                            };
                            CurrentViewName = GetViewName(nextView);
                            LastRequestedView = nextView;
                            cts.Cancel(); return;
                        }
                    }

                    // Check pending build result from background task
                    var buildResult = SlashCommands.PendingBuildResult;
                    if (buildResult != null)
                    {
                        SlashCommands.PendingBuildResult = null;
                        _history.Add(("cmd", null, buildResult, null));
                    }

                    // （Picker 渲染已移至上方与 UpdateMessages 合并，避免闪烁）

                    // Process the next queued message (if not already busy)
                    if (!_processing && _messageQueue.Reader.TryRead(out var msg))
                    {
                        _processing = true;
                        _responseCts = new CancellationTokenSource();
                        await StreamResponseAsync(msg).ConfigureAwait(false);
                        _processing = false;
                        continue;
                    }

                    // Yield to avoid 100% CPU spin when queue is empty
                    try { await Task.Delay(16, cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            });
        }
        catch (OperationCanceledException) { }
        result = LastRequestedView;
        return result;
    }

    // ── 消息面板 ──

    void IStreamerHost.UpdateMessages(string s) => UpdateMessages(s);
    private void UpdateMessages(string streamingContent)
    {
        var rv = Volatile.Read(ref _renderVersion);
        var hv = Volatile.Read(ref _historyVersion);
        if (rv > hv)
        {
            // Clear all rendered entries so next BuildMessagesPanel rebuilds them
            Interlocked.Exchange(ref _historyVersion, rv);
            for (int i = 0; i < _history.Count; i++)
                _history[i] = (_history[i].role, null, _history[i].rawContent, _history[i].reasoning);
        }
        var panel = _renderer.BuildMessagesPanel(
            _history, streamingContent, _toolCalls,
            _scrollOffset, _maxVisibleMessages, _expandedMessages);
        _messagesLayout.Update(panel);
    }

    // ── 命令面板 Overlay ──

    private static Panel BuildPickerOverlay(string filter, List<SlashCommands.SuggestionItem> items, int selIdx)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[bold {ThemeService.PrimaryTag}]/[/][bold]{filter.EscapeMarkup()}[/]");
        var displayed = items.Take(10).ToList();
        foreach (var (item, i) in displayed.Select((it, idx) => (it, idx)))
        {
            var cmd = item.Completion;
            var display = item.DisplayText;
            if (i == selIdx)
                sb.AppendLine($"[black on {ThemeService.PrimaryTag}]  {display,-30}[/]");
            else
                sb.AppendLine($"  [{ThemeService.MutedTag}]{display,-30}[/]");
        }
        if (items.Count > 10)
            sb.AppendLine($"[{ThemeService.MutedTag}]... 还有 {items.Count - 10} 项[/]");
        sb.AppendLine($"[{ThemeService.MutedTag}]↑↓=选择  Tab=补全  Enter=执行  Esc=取消[/]");

        return new Panel(
            Align.Center(new Markup(sb.ToString().TrimEnd()), VerticalAlignment.Middle))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(ThemeService.BorderColor),
            Padding = new Padding(2, 1, 2, 1),
            Expand = true,
        };
    }

    // ═══════════════════════════════════════════
    //  View names & switcher
    // ═══════════════════════════════════════════

    internal static readonly (string key, string name, TuiView view)[] ViewOptions =
    [
        ("1", "聊天", TuiView.Chat),
        ("2", "仪表盘", TuiView.Dashboard),
        ("3", "LLM 配置", TuiView.LLMConfig),
        ("4", "文本编辑器", TuiView.TextPad),
        ("5", "技能", TuiView.Skills),
        ("6", "会话", TuiView.Sessions),
        ("7", "任务", TuiView.Jobs),
        ("8", "记忆浏览", TuiView.MemoryBrowser),
        ("9", "工作流", TuiView.Workflows),
        ("0", "图谱", TuiView.GraphBrowser),
    ];

    public static string GetViewName(TuiView view) => view switch
    {
        TuiView.Chat => "聊天",
        TuiView.Dashboard => "仪表盘",
        TuiView.LLMConfig => "LLM 配置",
        TuiView.TextPad => "文本编辑器",
        TuiView.Skills => "技能",
        TuiView.Sessions => "会话",
        TuiView.Jobs => "任务",
        TuiView.MemoryBrowser => "记忆浏览",
        TuiView.Workflows => "工作流",
        TuiView.GraphBrowser => "图谱",
        _ => "聊天",
    };

    private Panel BuildViewSwitcherOverlay()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[bold {ThemeService.PrimaryTag}]视图切换[/]  (Ctrl+P)");
        foreach (var (key, name, _) in ViewOptions)
        {
            var selected = _viewPickerSelected == Array.IndexOf(ViewOptions, (key, name, TuiView.Chat));
            if (key == _viewPickerSelected.ToString())
                selected = true;
            if (selected)
                sb.AppendLine($"[black on {ThemeService.PrimaryTag}]  [{key}] {name,-16}[/]");
            else
                sb.AppendLine($"  [{ThemeService.MutedTag}] [{key}] {name,-16}[/]");
        }
        sb.AppendLine($"[{ThemeService.MutedTag}]↑↓=选择  Enter=切换  Esc=取消[/]");

        var selectedIdx = Math.Clamp(_viewPickerSelected, 0, ViewOptions.Length - 1);
        _viewPickerSelected = selectedIdx;

        return new Panel(
            Align.Center(new Markup(sb.ToString().TrimEnd()), VerticalAlignment.Middle))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(ThemeService.BorderColor),
            Padding = new Padding(2, 1, 2, 1),
            Expand = true,
        };
    }

    // ── Footer ──

    void IStreamerHost.UpdateFooter(string p, string s) => UpdateFooter(p, s);
    private void UpdateFooter(string pickerText, string statusText, bool isFirstEmpty = false,
        List<SlashCommands.SuggestionItem>? suggestions = null, int selIdx = -1)
    {
        var startupMsg = _startupMessage;
        if (startupMsg != null) _startupMessage = null;
        var sm = _statusMessage;
        if (sm != null) { _statusMessage = null; statusText = sm; }
        var panel = _renderer.BuildFooter(
            pickerText, statusText, isFirstEmpty,
            _inputLines, _cursorLine, _cursorCol, MaxInputLines,
            suggestions, selIdx, startupMsg);
        _footerLayout.Update(panel);
    }

    // ── 流式响应 ──

    private async Task StreamResponseAsync(string input)
    {
        using var cts = new CancellationTokenSource();
        _responseCts = cts;

        var streamer = new ResponseStreamer(
            _chat, _renderer, _sessions, _layout, _liveCtx!, _questionService,
            _history, _toolCalls, this);
        await streamer.StreamAsync(input, cts).ConfigureAwait(false);
    }

    // ── IStreamerHost ──

    Task IStreamerHost.SaveSessionAsync() => SaveSessionAsync();
    (bool found, bool success, string output, string error) IStreamerHost.TryParseToolResult(string text)
        => ToolResultParser.Parse(text);
    (string title, string message, string extraInfo)? IStreamerHost.TryParseConfirmRequest(string text)
        => ConfirmRequestParser.Parse(text);
    QuestionPost? IStreamerHost.GetPendingQuestion()
    {
        var qp = _pendingQuestion;
        _pendingQuestion = null;
        return qp;
    }
    Task? IStreamerHost.ExtractMemory(string userInput)
        => _memoryExtractor?.ExtractFromTurnAsync(userInput, ct: CancellationToken.None);

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";

    private static int SafeWindowHeight
    {
        get { try { return Console.WindowHeight; } catch { return 24; } }
    }

    private static string FormatElapsed(TimeSpan t) =>
        t.TotalSeconds >= 60 ? $"{(int)t.TotalMinutes}m{t.Seconds}s" : $"{t.TotalSeconds:F1}s";
}
