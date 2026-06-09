using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using LTAI.Agent;
using LTAI.Agent.Streaming;
using LTAI.Core.Configuration;
using LTAI.Core.Rendering;
using LTAI.Core.Session;
using LTAI.Agent.Tools;
using LTAI.TUI.Rendering;
using LTAI.TUI.Services;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LTAI.TUI;

using static ThemeService;

public sealed partial class ChatLayout : IDisposable, IStreamerHost, IChatRenderer
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
    internal int _pickerScrollOffset;
    private LiveDisplayContext? _liveCtx;
    public TuiView? LastRequestedView { get; private set; }
    public static string CurrentViewName { get; set; } = "聊天";
    public string? CurrentFileForContext => _textPadView.CurrentFileForContext;
    private int _subagentProgress;
    internal string? _statusMessage;
    internal string? _pendingChatRequest;
    internal string? _pendingSearchTerm;
    internal int _cacheVersion;
    internal int _historyVersion;
    internal volatile bool _needsRefresh;
    internal volatile bool _cascadeActive;
    internal volatile bool _modelPickerActive;
    internal List<string> _modelPickerItems = [];
    internal int _modelPickerSelectedIdx;
    internal string _modelPickerLayer = "";
    internal string _modelPickerProvider = "";
    internal string _modelPickerInputBuffer = "";
    internal string _modelPickerApiKeyEnvVar = "";
    internal string _modelPickerApiKeyProvider = "";

    // ── 文本输入覆盖层（替代 AnsiConsole.Prompt） ──
    internal volatile bool _textInputActive;
    internal string _textInputPrompt = "";
    internal string _textInputBuffer = "";
    internal bool _textInputIsSecret;
    internal string _textInputPrefix = "";
    internal TaskCompletionSource<string?>? _textInputTcs;

    // ── Question overlay (replaces QuestionFormView AnsiConsole.Prompt) ──
    internal volatile bool _questionActive;
    internal TaskCompletionSource<IReadOnlyList<string>>? _questionTcs;
    internal QuestionPrompt? _currentQuestionPrompt;
    internal string _statusText = "";
    internal int _currentQuestionIdx;
    internal int _currentQuestionTotal;
    internal string _questionInput = "";
    internal List<string> _questionMultiSelection = [];

    private static readonly object ConfirmLock = new();
    internal static TaskCompletionSource<ConfirmChoice>? PendingConfirmTcs
    {
        get { lock (ConfirmLock) return _pendingConfirmTcs; }
        set { lock (ConfirmLock) _pendingConfirmTcs = value; }
    }
    internal static string? PendingConfirmDetails;
    internal static string? PendingConfirmTitle;
    internal static string? PendingConfirmMessage;
    internal static string? PendingConfirmExtra;
    internal static volatile int ConfirmSelection;
    private static TaskCompletionSource<ConfirmChoice>? _pendingConfirmTcs;
    public static string? EditMode { get; set; }
    public static Func<bool>? TryUndoCallback { get; set; }
    private const int MaxHistory = 200;
    private const int RefreshIntervalMs = 33;
    private DateTime _lastRefresh = DateTime.MinValue;
    private DateTime _lastCmdTime = DateTime.MinValue;
    private readonly int _maxVisibleMessages;
    private readonly Action<int> _onSubagentComplete;
    private readonly Action<int, string, string> _onSubagentMessage;

    void IStreamerHost.ThrottledRefresh() => InvalidateRendered();

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

    void IStreamerHost.AutoCompact() => AutoCompact();
    internal void AutoCompact()
    {
        lock (_historyLock)
        {
            if (_history.Count <= 4) return;
            SnapshotForUndo();
            var keep = _history.GetRange(_history.Count - 4, 4);
            _history.Clear();
            _history.AddRange(keep);
            _history.Add(("cmd", null, "[green]已自动压缩历史，保留最近 2 轮对话[/]", null));
        }
        _ = Task.Run(async () =>
        {
            try { await SaveSessionAsync().ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ChatLayout] AutoCompact session save failed: {ex}"); }
        });
        InvalidateRendered();
    }

    void IStreamerHost.InvalidateRendered() => InvalidateRendered();
    internal void InvalidateRendered()
    {
        _needsRefresh = true;
        Interlocked.Increment(ref _cacheVersion);
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
                new Layout("Footer").Size(6));
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

        // Register UserInputService handler — routes tool-requested input to overlay
        UserInputService.PromptAsync = async (prompt, isSecret) =>
        {
            var tcs = new TaskCompletionSource<string?>();
            _textInputPrompt = prompt;
            _textInputIsSecret = isSecret;
            _textInputBuffer = "";
            _textInputTcs = tcs;
            _textInputActive = true;
            InvalidateRendered();
            try { return await tcs.Task.ConfigureAwait(false); }
            finally { _textInputActive = false; _textInputTcs = null; InvalidateRendered(); }
        };

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
                    try
                    {
                        while (!cts.Token.IsCancellationRequested)
                        {
                            Input.MouseTracker.InputEvent evt;
                            try
                            {
                                evt = Input.MouseTracker.ReadNext(cts.Token);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[ChatLayout] ReadNext FAILED: {ex}");
                                _statusMessage = $"[red]输入错误: {ex.Message.EscapeMarkup()}[/]";
                                InvalidateRendered();
                                await Task.Delay(1000, cts.Token).ConfigureAwait(false);
                                continue;
                            }

                            if (evt.KeyInfo.HasValue)
                            {
                                try
                                {
                                    var keepGoing = await keyDispatcher.HandleKeyAsync(evt.KeyInfo.Value, cts.Token).ConfigureAwait(false);
                                    if (!keepGoing) { cts.Cancel(); return; }
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[ChatLayout] HandleKeyAsync FAILED: {ex}");
                                    _statusMessage = $"[red]按键处理错误: {ex.Message.EscapeMarkup()}[/]";
                                    InvalidateRendered();
                                }
                            }
                            else if (evt.ScrollDelta != 0)
                            {
                                var oldOffset = _scrollOffset;
                                _scrollOffset = Math.Clamp(_scrollOffset - evt.ScrollDelta, 0, Math.Max(0, _history.Count - 1));
                                if (_scrollOffset != oldOffset)
                                    InvalidateRendered();
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
                                InvalidateRendered();
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ChatLayout] Input task CRASHED: {ex}");
                        _statusMessage = $"[red]输入线程崩溃: {ex.Message.EscapeMarkup()}[/]";
                        InvalidateRendered();
                    }
                    Input.MouseTracker.Disable();
                }, cts.Token);

                // Force initial render — main loop's doRefresh is false on first
                // iteration (nothing dirty yet), so the welcome screen never appears
                // without this explicit refresh.
                lock (_layout) { UpdateMessages(""); UpdateFooter("", "", true); ctx.Refresh(); }

                // Main rendering + processing loop: only refreshes on actual
                // changes. Cursor blink is handled independently by FooterRenderer
                // via Environment.TickCount — no periodic refresh timer needed.
                // This avoids flickering with IME input on Windows Terminal.
                while (!cts.Token.IsCancellationRequested)
                {
                    var doRefresh = _needsRefresh || _processing
                        || _viewPickerActive || _pickerActive || _cascadeActive
                        || _modelPickerActive || _textInputActive || _questionActive;

                    if (doRefresh)
                    {
                        _needsRefresh = false;

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
                            int selIdx, scrollOffset;
                            lock (_pickerLock)
                            {
                                filter = _pickerFilter;
                                items = _pickerItems;
                                selIdx = _pickerSelectedIdx;
                                scrollOffset = _pickerScrollOffset;
                            }
                            lock (_layout)
                            {
                                _messagesLayout.Update(BuildPickerOverlay(filter, items, selIdx, scrollOffset));
                                UpdateFooter("", "", showWatermark);
                                ctx.Refresh();
                            }
                        }
                        else if (_cascadeActive)
                        {
                            lock (_layout)
                            {
                                _messagesLayout.Update(BuildCascadeOverlay());
                                UpdateFooter("", "", showWatermark);
                                ctx.Refresh();
                            }
                        }
                        else if (_modelPickerActive)
                        {
                            lock (_layout)
                            {
                                _messagesLayout.Update(BuildModelPickerOverlay());
                                UpdateFooter("", "", showWatermark);
                                ctx.Refresh();
                            }
                        }
                        else if (_textInputActive)
                        {
                            lock (_layout)
                            {
                                _messagesLayout.Update(BuildTextInputOverlay());
                                UpdateFooter("", "", showWatermark);
                                ctx.Refresh();
                            }
                        }
                        else if (_questionActive)
                        {
                            lock (_layout)
                            {
                                _messagesLayout.Update(BuildQuestionOverlay());
                                UpdateFooter("", "", showWatermark);
                                ctx.Refresh();
                            }
                        }
                        else if (_processing)
                        {
                            // During streaming, the streaming thread updates _messagesLayout
                            // via UpdateMessages(content) and the spin animation updates footer via
                            // UpdateFooter — both signal via InvalidateRendered(). Main loop
                            // also updates footer with current input state for cursor visibility.
                            UpdateMessages("");
                            UpdateFooter("", "");
                            ctx.Refresh();
                        }
                        else
                        {
                            if (_history.Any(x => x.role == "cmd"))
                            {
                                var hasUserMsg = _history.Any(x => x.role == "user");
                                var expired = (DateTime.UtcNow - _lastCmdTime).TotalSeconds > 30;
                                if (hasUserMsg || expired)
                                    _history.RemoveAll(x => x.role == "cmd");
                            }
                            lock (_layout) { UpdateMessages(""); UpdateFooter("", "", showWatermark); ctx.Refresh(); }
                        }
                        showWatermark = false;
                    }

                    // Check quick-navigation from input thread
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

                    // Check pending build result from background task
                    // (includes cascade/command result text that needs rendering)
                    var buildResult = SlashCommands.PendingBuildResult;
                    if (buildResult != null)
                    {
                        SlashCommands.PendingBuildResult = null;
                        _history.Add(("cmd", null, buildResult, null));
                        InvalidateRendered();
                    }

                    // Process the next queued message (if not already busy)
                    if (!_processing && _messageQueue.Reader.TryRead(out var msg))
                    {
                        if (msg.StartsWith("/!") && msg.Length > 2)
                        {
                            // Slash command queued from input task — handle on main thread
                            var cmd = msg[2..];
                            try
                            {
                                var handled = await HandleSlashCommandAsync(cmd).ConfigureAwait(false);
                                if (!handled) { cts.Cancel(); return; }
                                var pending = SlashCommands.PendingInput;
                                if (pending != null)
                                {
                                    SlashCommands.PendingInput = null;
                                    SetInput(pending);
                                }
                                InvalidateRendered();
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[ChatLayout] Slash command error: {ex}");
                                _statusMessage = $"[red]命令错误: {ex.Message.EscapeMarkup()}[/]";
                                InvalidateRendered();
                            }
                        }
                        else
                        {
                            _processing = true;
                            _responseCts = new CancellationTokenSource();
                            _ = StreamResponseAsync(msg).ContinueWith(t =>
                            {
                                if (t.IsFaulted)
                                {
                                    var ex = t.Exception?.InnerException;
                                    System.Diagnostics.Debug.WriteLine($"[ChatLayout] StreamResponse failed: {ex}");
                                    _statusMessage = $"[red]流式错误: {ex?.Message.EscapeMarkup() ?? "未知"}[/]";
                                }
                                _processing = false;
                                InvalidateRendered();
                            }, TaskScheduler.Default);
                            continue;
                        }
                        continue;
                    }

                    // Yield to avoid 100% CPU spin when queue is empty
                    try { await Task.Delay(doRefresh ? 16 : 50, cts.Token).ConfigureAwait(false); }
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
        var cv = Volatile.Read(ref _cacheVersion);
        var hv = Volatile.Read(ref _historyVersion);
        if (cv > hv)
        {
            // Clear all rendered entries so next BuildMessagesPanel rebuilds them
            Interlocked.Exchange(ref _historyVersion, cv);
            for (int i = 0; i < _history.Count; i++)
                _history[i] = (_history[i].role, null, _history[i].rawContent, _history[i].reasoning);
        }
        var panel = _renderer.BuildMessagesPanel(
            _history, streamingContent, _toolCalls,
            _scrollOffset, _maxVisibleMessages, _expandedMessages);
        _messagesLayout.Update(panel);
    }

    // ── 命令面板 Overlay ──

    internal int CalculatePickerWindowSize()
    {
        const int maxLines = 12;
        var used = 2; // filter line + hint line
        var itemsRendered = 0;

        if (_pickerScrollOffset > 0) used++;

        var windowItems = _pickerItems.Skip(_pickerScrollOffset).ToList();
        var grouped = windowItems.GroupBy(i => i.Group).OrderBy(g => g.Key).ToList();

        foreach (var group in grouped)
        {
            if (used >= maxLines) break;
            used++;
            foreach (var _ in group)
            {
                if (used >= maxLines) break;
                used++;
                itemsRendered++;
            }
        }

        return Math.Max(itemsRendered, 1);
    }

    private static IRenderable BuildPickerOverlay(string filter, List<SlashCommands.SuggestionItem> items, int selIdx, int scrollOffset)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[bold {ThemeService.PrimaryTag}]/[/][bold]{filter.EscapeMarkup()}[/]");
        sb.AppendLine($"[{ThemeService.MutedTag}]↑↓ 选择  Tab 补全  Enter 执行  Esc 取消[/]");

        var windowItems = items.Skip(scrollOffset).ToList();
        var grouped = windowItems
            .GroupBy(i => i.Group)
            .OrderBy(g => g.Key)
            .ToList();

        const int maxLines = 12;
        var rendered = 0;
        var flatIdx = scrollOffset;

        if (scrollOffset > 0)
        {
            sb.AppendLine($"  [{ThemeService.MutedTag}]↑ {scrollOffset} 项...[/]");
            rendered++;
        }

        foreach (var group in grouped)
        {
            if (rendered >= maxLines) break;

            sb.AppendLine($"  [bold]{group.Key.EscapeMarkup()}[/]");
            rendered++;

            foreach (var item in group)
            {
                if (rendered >= maxLines) break;

                var display = item.DisplayText;
                if (flatIdx == selIdx)
                    sb.AppendLine($"[black on {ThemeService.PrimaryTag}]    {display}[/]");
                else
                    sb.AppendLine($"    [{ThemeService.MutedTag}]{display}[/]");

                flatIdx++;
                rendered++;
            }
        }

        var remaining = items.Count - flatIdx;
        if (remaining > 0)
            sb.AppendLine($"[{ThemeService.MutedTag}]↓ {remaining} 项...[/]");

        var panel = new Panel(
            Align.Left(new Markup(sb.ToString().TrimEnd()), VerticalAlignment.Middle))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(ThemeService.BorderColor),
            Padding = new Padding(2, 1, 2, 1),
        };
        return Align.Center(panel, VerticalAlignment.Middle);
    }

    // ── 级联菜单 Overlay ──

    private static IRenderable BuildCascadeOverlay()
    {
        var path = SlashCommands.CascadeStack.Length > 0
            ? $"/{SlashCommands.CascadeCmd} {string.Join(" ", SlashCommands.CascadeStack)}"
            : $"/{SlashCommands.CascadeCmd}";
        var sb = new StringBuilder();
        sb.AppendLine($"[bold yellow]{path}[/]");
        for (int i = 0; i < SlashCommands.CascadeItems.Length; i++)
        {
            var parts = SlashCommands.CascadeItems[i].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var s = parts[0];
            var desc = parts.Length > 1 ? parts[1] : "";
            var display = $"{s}  {desc}".TrimEnd();
            if (i == SlashCommands.CascadeSel)
                sb.AppendLine($"[black on yellow]  {display,-40}[/]");
            else
                sb.AppendLine($"  [{ThemeService.MutedTag}]{display,-40}[/]");
        }
        sb.Append($"[{ThemeService.MutedTag}]↑↓=选择  Enter=确认  Esc=返回  Ctrl+Q=退出[/]");

        var panel = new Panel(
            Align.Left(new Markup(sb.ToString().TrimEnd()), VerticalAlignment.Middle))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(ThemeService.BorderColor),
            Padding = new Padding(4, 2, 4, 2),
        };
        return Align.Center(panel, VerticalAlignment.Middle);
    }

    // ── 模型选择器 Overlay ──

    private IRenderable BuildModelPickerOverlay()
    {
        var sb = new StringBuilder();

        if (_modelPickerItems.Count == 0 && !string.IsNullOrEmpty(_modelPickerApiKeyEnvVar))
        {
            var masked = new string('•', _modelPickerInputBuffer.Length);
            sb.AppendLine($"[bold yellow]{_modelPickerLayer.ToUpperInvariant()} — {_modelPickerProvider}[/]");
            sb.AppendLine();
            sb.AppendLine($"  [dim]设置 {_modelPickerApiKeyEnvVar}:[/]");
            sb.AppendLine($"  [yellow]{masked}[/]");
            sb.AppendLine();
            sb.AppendLine($"  [{ThemeService.MutedTag}]输入 API Key  Enter=确认  Esc=取消[/]");
        }
        else
        {
            sb.AppendLine($"[bold yellow]{_modelPickerLayer.ToUpperInvariant()} — {_modelPickerProvider}[/]");
            sb.AppendLine($"[{ThemeService.MutedTag}]↑↓ 选择  Enter 确认  Esc 取消[/]");

            for (int i = 0; i < _modelPickerItems.Count; i++)
            {
                var name = _modelPickerItems[i];
                if (i == _modelPickerSelectedIdx)
                    sb.AppendLine($"[black on yellow]  {name,-20}[/]");
                else
                    sb.AppendLine($"  [{ThemeService.MutedTag}]{name,-20}[/]");
            }
        }

        var panel = new Panel(
            Align.Left(new Markup(sb.ToString().TrimEnd()), VerticalAlignment.Middle))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(ThemeService.BorderColor),
            Padding = new Padding(4, 2, 4, 2),
        };
        return Align.Center(panel, VerticalAlignment.Middle);
    }

    // ═══════════════════════════════════════════
    //  Text input overlay (replaces AnsiConsole.Prompt)
    // ═══════════════════════════════════════════

    private IRenderable BuildTextInputOverlay()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[bold yellow]{_textInputPrompt}[/]");
        var masked = _textInputIsSecret
            ? new string('•', _textInputBuffer.Length)
            : _textInputBuffer;
        var cursor = (DateTime.UtcNow.Millisecond % 1000) < 500 ? '▌' : ' ';
        sb.AppendLine($"  [cyan]{masked}{cursor}[/]");
        sb.AppendLine($"[{ThemeService.MutedTag}]Enter=确认  Esc=取消[/]");

        var panel = new Panel(
            Align.Left(new Markup(sb.ToString().TrimEnd()), VerticalAlignment.Middle))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(ThemeService.BorderColor),
            Padding = new Padding(4, 2, 4, 2),
        };
        return Align.Center(panel, VerticalAlignment.Middle);
    }

    // ═══════════════════════════════════════════
    //  Question overlay (replaces QuestionFormView AnsiConsole.Prompt)
    // ═══════════════════════════════════════════

    private IRenderable BuildQuestionOverlay()
    {
        var q = _currentQuestionPrompt;
        if (q == null) return new Markup("");

        var sb = new StringBuilder();
        sb.AppendLine($"[bold yellow]❓ 问题 {_currentQuestionIdx + 1}/{_currentQuestionTotal}[/]");
        sb.AppendLine($"[bold]{q.Header.EscapeMarkup()}[/]");
        sb.AppendLine($"[grey]{q.Question.EscapeMarkup()}[/]");
        sb.AppendLine();

        if (q.Options.Count > 0)
        {
            for (int j = 0; j < q.Options.Count; j++)
            {
                var opt = q.Options[j];
                var key = q.Multiple ? $"[{j + 1}]" : $"{(char)('a' + j)}";
                var selected = q.Multiple && _questionMultiSelection.Contains(opt.Label);
                var marker = selected ? "[green]✓[/]" : " ";
                sb.AppendLine($"  {marker} [cyan]{key}[/] {opt.Label.EscapeMarkup()}");
                if (!string.IsNullOrEmpty(opt.Description))
                    sb.AppendLine($"     [dim]{opt.Description.EscapeMarkup()}[/]");
            }
            sb.AppendLine();

            if (q.Multiple)
            {
                var selText = _questionMultiSelection.Count > 0
                    ? string.Join(", ", _questionMultiSelection)
                    : "(无)";
                sb.AppendLine($"[dim]已选: {selText}[/]");
                sb.AppendLine($"[grey]输入序号切换 (1,2,3...), Enter 确认, c=自定义回答[/]");
            }
            else
            {
                var cursor = (DateTime.UtcNow.Millisecond % 1000) < 500 ? '▌' : ' ';
                sb.AppendLine($"[grey]按字母选择 ({string.Join("/", q.Options.Select((_, i) => $"{(char)('a' + i)}"))}){cursor}[/]");
                sb.AppendLine($"[grey]c=自定义回答  Enter=确认[/]");
            }
        }
        else
        {
            var masked = new string('•', _questionInput.Length);
            var cursor = (DateTime.UtcNow.Millisecond % 1000) < 500 ? '▌' : ' ';
            sb.AppendLine($"  [cyan]{masked}{cursor}[/]");
            sb.AppendLine($"[grey]输入回答 (Enter 确认)[/]");
        }

        var panel = new Panel(
            Align.Left(new Markup(sb.ToString().TrimEnd()), VerticalAlignment.Middle))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(ThemeService.BorderColor),
            Padding = new Padding(4, 2, 4, 2),
        };
        return Align.Center(panel, VerticalAlignment.Middle);
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

    private IRenderable BuildViewSwitcherOverlay()
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

        var panel = new Panel(
            Align.Left(new Markup(sb.ToString().TrimEnd()), VerticalAlignment.Middle))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(ThemeService.BorderColor),
            Padding = new Padding(2, 1, 2, 1),
        };
        return Align.Center(panel, VerticalAlignment.Middle);
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

    internal void ReRenderConfirmation()
    {
        ConfirmationModal.RenderModal(_layout,
            PendingConfirmTitle ?? "", PendingConfirmMessage ?? "",
            PendingConfirmDetails ?? "", PendingConfirmExtra);
        InvalidateRendered();
    }

    // ── 流式响应 ──

    private async Task StreamResponseAsync(string input)
    {
        using var cts = new CancellationTokenSource();
        _responseCts = cts;

        var streamer = new ChatStreamer(
            _chat, this, _sessions, _questionService)
        {
            OnAskQuestions = async (post, ct) =>
            {
                var qf = new QuestionFormView(this, _questionService);
                await qf.ShowAsync(post, ct).ConfigureAwait(false);
            }
        };
        await streamer.StreamAsync(input, cts).ConfigureAwait(false);
    }

    // ── IChatRenderer ──

    void IChatRenderer.OnStreamStart() { }
    void IChatRenderer.OnTextDelta(string delta) => UpdateMessages(delta);
    void IChatRenderer.OnToolCall(string name, string? arguments) { }
    void IChatRenderer.OnToolResult(string name, string result, bool success) { }
    void IChatRenderer.OnStreamEnd() { }

    void IChatRenderer.RenderMessage(string role, string content,
        IReadOnlyList<ToolCallRecord>? toolCalls, string? reasoning)
    {
        lock (_historyLock) { _history.Add((role, null, content, reasoning)); }
        InvalidateRendered();
    }

    void IChatRenderer.UpdateStatus(string text) => UpdateFooter("", text);

    void IChatRenderer.UpdateProgress(string frame, string text, string? elapsed) =>
        UpdateFooter("", string.IsNullOrEmpty(frame) ? text : $"{frame} {text}");

    ToolResultInfo IChatRenderer.TryParseToolResult(string text)
    {
        var r = ToolResultParser.Parse(text);
        return new ToolResultInfo(r.found, r.success, r.output, r.error);
    }

    ConfirmRequest? IChatRenderer.TryParseConfirmRequest(string text)
    {
        var r = ConfirmRequestParser.Parse(text);
        return r is var (t, m, e) ? new ConfirmRequest(t, m, e) : null;
    }

    async Task<string?> IChatRenderer.PromptUserAsync(string prompt, bool isSecret)
    {
        var tcs = new TaskCompletionSource<string?>();
        _textInputPrompt = prompt;
        _textInputIsSecret = isSecret;
        _textInputBuffer = "";
        _textInputTcs = tcs;
        _textInputActive = true;
        InvalidateRendered();
        try { return await tcs.Task.ConfigureAwait(false); }
        finally { _textInputActive = false; _textInputTcs = null; InvalidateRendered(); }
    }

    async Task<ConfirmChoice> IChatRenderer.ShowConfirmAsync(
        string title, string message, string result, string extraInfo)
    {
        var layout = _layout;
        return await ConfirmationModal.ShowInlineAsync(layout,
            () => InvalidateRendered(), title, message, result, extraInfo).ConfigureAwait(false);
    }

    void IChatRenderer.TrimHistory() { TrimHistory(); }
    void IChatRenderer.AutoCompact() { AutoCompact(); }
    Task IChatRenderer.SaveSessionAsync() => SaveSessionAsync();

    async Task IChatRenderer.ExtractMemoryAsync(string userInput)
    {
        if (_memoryExtractor != null)
            await _memoryExtractor.ExtractFromTurnAsync(userInput, ct: CancellationToken.None)
                .ConfigureAwait(false);
    }

    void IChatRenderer.RequestRender() => InvalidateRendered();
    void IChatRenderer.InvalidateRender() => InvalidateRendered();

    string IChatRenderer.CurrentStatus
    {
        get => _statusText;
        set => _statusText = value;
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
