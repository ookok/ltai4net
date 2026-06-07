using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using LTAI.Agent;
using LTAI.Core.Configuration;
using LTAI.Core.Session;
using LTAI.Agent.Tools;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LTAI.TUI;

public sealed class ChatLayout : IDisposable
{
    private readonly ChatAgent _chat;
    private readonly Rendering.ChatRenderer _renderer;
    internal readonly List<(string role, IRenderable? rendered, string rawContent, string? reasoning)> _history = new();
    internal readonly object _historyLock = new();
    private readonly Layout _layout;
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
    internal readonly object _pickerLock = new();
    internal string _pickerFilter = "";
    internal List<SlashCommands.SuggestionItem> _pickerItems = new();
    internal int _pickerSelectedIdx;
    private LiveDisplayContext? _liveCtx;
    public TuiView? LastRequestedView { get; private set; }
    public string? CurrentFileForContext => _textPadView.CurrentFileForContext;
    private int _subagentProgress;
    private const int MaxHistory = 200;
    private const int RefreshIntervalMs = 33;
    private DateTime _lastRefresh = DateTime.MinValue;
    private DateTime _lastCmdTime = DateTime.MinValue;
    private readonly int _maxVisibleMessages;
    private readonly Action<int> _onSubagentComplete;
    private readonly Action<int, string, string> _onSubagentMessage;

    private void ThrottledRefresh()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastRefresh).TotalMilliseconds >= RefreshIntervalMs && _liveCtx != null)
        {
            _liveCtx.Refresh();
            _lastRefresh = now;
        }
    }

    internal void TrimHistory()
    {
        if (_history.Count <= MaxHistory) return;
        var removeCount = _history.Count - MaxHistory;
        _history.RemoveRange(0, removeCount);
    }

    /// <summary>将所有已渲染 Panel 标记为失效，下次 UpdateMessages 会重新构建。</summary>
    internal void InvalidateRendered()
    {
        for (int i = 0; i < _history.Count; i++)
            _history[i] = (_history[i].role, null, _history[i].rawContent, _history[i].reasoning);
    }

    public ChatLayout(ChatAgent chat, Rendering.ChatRenderer renderer, QuestionService? questionService = null,
        SessionManager? sessions = null, TextPadView? textPadView = null,
        LTAI.Agent.Memory.PalaceStore? palaceStore = null)
    {
        _chat = chat;
        _renderer = renderer;
        _sessions = sessions ?? new SessionManager();
        _textPadView = textPadView ?? new TextPadView(Directory.GetCurrentDirectory());
        _questionService = questionService ?? new QuestionService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<QuestionService>.Instance);
        _questionService.QuestionPosted += post => _pendingQuestion = post;

        // 自动计算可见消息数，确保输入区始终固定
        // 每条 Panel ≈ 4 行，Header 2 行，Footer 6 行
        var termHeight = Math.Max(24, Console.WindowHeight);
        _maxVisibleMessages = Math.Max(3, (termHeight - 10) / 4);

        _layout = new Layout()
            .SplitRows(
                new Layout("Header").Size(2),
                new Layout("Messages"),
                new Layout("Footer").Size(10));

        _layout["Header"].Update(
            new Panel("[bold]LTAI 聊天[/] — [grey]Esc=退出  SEnter=发送  Enter=换行  ↑↓=光标  S↑↓=滚屏  Ctrl+V=粘贴  1-5=视图  /help=帮助[/]")
                .Border(BoxBorder.None).Expand());

        _layout["Messages"].Update(
            new Panel(
                "[bold yellow]💬 欢迎使用 LTAI[/]\n\n" +
                "[grey]可用命令:[/]\n" +
                "  [cyan]/new[/]     — 新建会话\n" +
                "  [cyan]/help[/]    — 显示帮助\n" +
                "  [cyan]/exit[/]    — 退出\n" +
                "  [cyan]/model[/]   — 管理模型\n" +
                "  [cyan]/config[/]  — 配置 LLM\n\n" +
                "[grey]快捷键:[/]\n" +
                "  [cyan]1-5[/]       — 切换视图\n" +
                "  [cyan]↑↓[/]       — 历史消息\n" +
                "  [cyan]/[/]         — 打开命令选择器\n\n" +
                "[dim]直接输入消息开始对话，或输入 [yellow]/[/] 浏览全部命令[/]")
                .Border(BoxBorder.Rounded)
                .Header(new PanelHeader("[bold yellow]💬 LTAI[/]"))
                .Expand());

        _layout["Footer"].Update(
            new Panel("[grey]等待首次请求...  输入消息开始对话[/]")
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
                    _ = warmupTask.ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                            System.Diagnostics.Debug.WriteLine($"WarmUp failed: {t.Exception?.InnerException?.Message}");
                    }, TaskContinuationOptions.OnlyOnFaulted);

                    try { await warmupTask.WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false); }
                    catch { }

                    ctx.Status = "[green]✓ 初始化完成[/]";
                    await Task.Delay(200);
                }).ConfigureAwait(false);
        }
        catch { }
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

                var blinkCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                var blinkTask = Task.Run(async () =>
                {
                    while (!blinkCts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(400, blinkCts.Token).ConfigureAwait(false);
                        UpdateFooter("", "", IsInputEmpty() && showWatermark);
                        try { _liveCtx?.Refresh(); } catch (ObjectDisposedException) { break; }
                    }
                }, blinkCts.Token);


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
                            // Click-to-scroll: just refresh to acknowledge
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
                    if (_pickerActive)
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
                            // Inline picker: 不替换 Messages，只在 Footer 显示建议
                            var footerBuf = string.IsNullOrEmpty(filter) ? "/" : "/" + filter;
                            UpdateFooter(footerBuf, "", showWatermark, items, selIdx);
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
                            LastRequestedView = _quickNav switch
                            {
                                '1' => TuiView.Dashboard, '3' => TuiView.LLMConfig,
                                '4' => TuiView.TextPad, '5' => TuiView.Skills, _ => TuiView.Chat
                            };
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

    // ── 消息面板（Panel 包裹每条消息） ──

    private void UpdateHeader()
    {
        var planStatus = LTAI.Agent.Tools.PlanTools.PlanStatus();
        var hasPlan = planStatus.Contains("Current Step") || planStatus.Contains("executing");
        var planTag = hasPlan ? "  [bold yellow]📋 计划执行中[/]" : "";

        var model = LTAI.Core.Configuration.UsageTracker.ActiveModel?.EscapeMarkup() ?? "--";
        var toolCount = LTAI.Core.Configuration.UsageTracker.ToolCalls;
        var errorIndicator = _textPadView.PendingChatRequest != null
            ? "  [bold red]🔴 错误待修复[/]"
            : "";

        _layout["Header"].Update(
            new Panel($"[bold]LTAI 聊天[/]{planTag}{errorIndicator} — [grey]{model}  |  🛠 {toolCount} 次工具调用  |  Esc=退出  Enter=发送  1-5=视图  /help=帮助[/]")
                .Border(BoxBorder.None).Expand());
    }

    private void UpdateMessages(string streamingContent)
    {
        // Delegate all panel rendering to ChatRenderer
        var panel = _renderer.BuildMessagesPanel(
            _history, streamingContent, _toolCalls,
            _scrollOffset, _maxVisibleMessages, _expandedMessages);
        _layout["Messages"].Update(panel);
    }

    // ── 多行输入帮助方法 ──

    internal bool IsInputEmpty() => _inputLines.Count == 1 && _inputLines[0].Length == 0;

    internal string GetInputText() => string.Join("\n", _inputLines);

    internal void ClearInput()
    {
        _inputLines.Clear();
        _inputLines.Add("");
        _cursorLine = 0;
        _cursorCol = 0;
    }

    internal void SetInput(string text)
    {
        ClearInput();
        var lines = text.Split('\n');
        _inputLines.Clear();
        _inputLines.AddRange(lines);
        _cursorLine = _inputLines.Count - 1;
        _cursorCol = _inputLines[^1].Length;
    }

    internal void InsertChar(char c)
    {
        var line = _inputLines[_cursorLine];
        _inputLines[_cursorLine] = line.Insert(_cursorCol, c.ToString());
        _cursorCol++;
    }

    internal void BackspaceChar()
    {
        if (_cursorCol > 0)
        {
            var line = _inputLines[_cursorLine];
            _inputLines[_cursorLine] = line.Remove(_cursorCol - 1, 1);
            _cursorCol--;
        }
        else if (_cursorLine > 0)
        {
            // Join with previous line
            var prevLine = _inputLines[_cursorLine - 1];
            _cursorCol = prevLine.Length;
            _inputLines[_cursorLine - 1] = prevLine + _inputLines[_cursorLine];
            _inputLines.RemoveAt(_cursorLine);
            _cursorLine--;
        }
    }

    /// <summary>替换输入内容为单行文本（用于历史导航等）</summary>
    internal void ReplaceInputLine(string text)
    {
        _inputLines.Clear();
        _inputLines.Add(text);
        _cursorLine = 0;
        _cursorCol = text.Length;
    }

    internal void SaveToHistory(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return;
        if (_inputHistory.Count > 0 && _inputHistory[^1] == input) return;
        _inputHistory.Add(input);
        if (_inputHistory.Count > 50) _inputHistory.RemoveAt(0);
        _historyIndex = -1;
    }

    /// <summary>检查并触发斜杠命令选择器</summary>
    internal bool CheckPickerTrigger()
    {
        if (_inputLines.Count == 1 && _inputLines[0] == "/")
        {
            lock (_pickerLock)
            {
                _pickerActive = true;
                _pickerFilter = "";
                _pickerItems = SlashCommands.GetSuggestionItems("/");
                _pickerSelectedIdx = _pickerItems.Count > 0 ? 0 : -1;
            }
            return true;
        }
        return false;
    }

    // ── 选定器辅助 ──

    /// <summary>根据当前 <c>_pickerFilter</c> 重新计算 <c>_pickerItems</c>。</summary>
    /// <remarks>调用方必须持有 <c>_pickerLock</c>。</remarks>
    internal void UpdatePickerItems()
    {
        var prefix = "/" + _pickerFilter;
        _pickerItems = prefix.Length > 1
            ? SlashCommands.GetSuggestionItems(prefix)
            : SlashCommands.GetSuggestionItems("/");
        if (_pickerSelectedIdx >= _pickerItems.Count) _pickerSelectedIdx = _pickerItems.Count - 1;
        if (_pickerSelectedIdx < 0 && _pickerItems.Count > 0) _pickerSelectedIdx = 0;
    }

    // ── Footer ──

    private void UpdateFooter(string pickerText, string statusText, bool isFirstEmpty = false,
        List<SlashCommands.SuggestionItem>? suggestions = null, int selIdx = -1)
    {
        var startupMsg = _startupMessage;
        if (startupMsg != null) _startupMessage = null;
        var panel = _renderer.BuildFooter(
            pickerText, statusText, isFirstEmpty,
            _inputLines, _cursorLine, _cursorCol, MaxInputLines,
            suggestions, selIdx, startupMsg);
        _layout["Footer"].Update(panel);
    }

    // ── 流式响应 ──

    private async Task StreamResponseAsync(string input)
    {
        using var cts = new CancellationTokenSource();
        _responseCts = cts;

        var streamer = new ResponseStreamer(
            _chat, _renderer, _sessions, _layout, _liveCtx!, _questionService,
            _history, _toolCalls,
            updateHeader: UpdateHeader,
            updateFooter: (p, s) => UpdateFooter(p, s),
            updateMessages: s => UpdateMessages(s),
            throttledRefresh: ThrottledRefresh,
            invalidateRendered: InvalidateRendered,
            trimHistory: TrimHistory,
            saveSessionAsync: SaveSessionAsync,
            tryParseToolResult: ParseToolResult,
            tryParseConfirmRequest: ParseConfirmRequest,
            getPendingQuestion: () =>
            {
                var qp = _pendingQuestion;
                _pendingQuestion = null;
                return qp;
            },
            extractMemory: _memoryExtractor != null
                ? (userInput) => _memoryExtractor.ExtractFromTurnAsync(userInput, ct: CancellationToken.None)
                : null);
        await streamer.StreamAsync(input, cts).ConfigureAwait(false);
    }

    // ── Slash 命令 ──

    internal async Task<bool> HandleSlashCommandAsync(string input)
    {
        if (string.Equals(input, "/new", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(input, "/clear", StringComparison.OrdinalIgnoreCase))
        {
            await SaveSessionAsync().ConfigureAwait(false);
            lock (_historyLock) _history.Clear();
            _toolCalls.Clear();
            _sessions.NewSession();
            return true;
        }
        if (input.StartsWith("/sessions", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("/session", StringComparison.OrdinalIgnoreCase))
        {
            await HandleSessionsCommandAsync(input).ConfigureAwait(false);
            return true;
        }
        if (string.Equals(input, "/exit", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(input, "/quit", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var cmdStatus = "";
        var running = true;
        _lastCmdTime = DateTime.UtcNow;
        if (SlashCommands.TryExecute(input, ref running, ref cmdStatus))
        {
            if (!string.IsNullOrEmpty(cmdStatus))
                lock (_historyLock) _history.Add(("cmd", null, cmdStatus, null));
            return running;
        }
        return true;
    }

    // ── 工具方法 ──

    // Wrapper for ResponseStreamer delegate
    private static (bool found, bool success, string output, string error) ParseToolResult(string text)
        => ToolResultParser.Parse(text);

    private static (string title, string message, string extraInfo)? ParseConfirmRequest(string text)
        => ConfirmRequestParser.Parse(text);

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";

    private static string FormatElapsed(TimeSpan t) =>
        t.TotalSeconds >= 60 ? $"{(int)t.TotalMinutes}m{t.Seconds}s" : $"{t.TotalSeconds:F1}s";

    // ── Session 持久化 ──

    private async Task SaveSessionAsync()
    {
        if (_sessions.CurrentHandle == null) return;
        await _sessions.SaveSessionAsync().ConfigureAwait(false);
    }

    private async Task HandleSessionsCommandAsync(string input)
    {
        var result = await _sessionHandler.ExecuteAsync(input, SaveSessionAsync).ConfigureAwait(false);

        foreach (var msg in result.HistoryMessages)
            _history.Add(("cmd", null, msg, null));

        if (result.LoadedMessages != null)
        {
            _history.Clear();
            _toolCalls.Clear();
            foreach (var (role, content) in result.LoadedMessages)
                _history.Add((role, null, content, null));
        }
    }
}
