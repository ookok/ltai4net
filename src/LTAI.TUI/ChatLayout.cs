using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using LTAI.Agent;
using LTAI.Core.Configuration;
using LTAI.Core.Session;
using LTAI.Agent.Tools;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LTAI.TUI;

public sealed class ChatLayout
{
    private readonly ChatAgent _chat;
    private readonly Rendering.ChatRenderer _renderer;
    internal readonly List<(string role, IRenderable? rendered, string rawContent, string? reasoning)> _history = new();
    private readonly Layout _layout;
    private readonly QuestionService _questionService;
    private readonly SessionManager _sessions;
    private volatile QuestionPost? _pendingQuestion;

    internal readonly System.Threading.Channels.Channel<string> _messageQueue =
        System.Threading.Channels.Channel.CreateBounded<string>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    internal bool _processing;
    internal CancellationTokenSource? _responseCts;
    internal volatile char _quickNav;
    private static string? _startupMessage;

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
    private int _toolCallCount;
    private int _subagentProgress;
    private const int MaxHistory = 200;
    private const int RefreshIntervalMs = 33;
    private DateTime _lastRefresh = DateTime.MinValue;
    private DateTime _lastCmdTime = DateTime.MinValue;
    private readonly int _maxVisibleMessages;

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
    private static readonly string[] PulseFrames = [
        "[deepskyblue1]⠋[/]",
        "[deepskyblue1]⠙[/]",
        "[deepskyblue1]⠹[/]",
        "[deepskyblue1]⠸[/]",
        "[deepskyblue1]⠼[/]",
        "[deepskyblue1]⠴[/]",
        "[deepskyblue1]⠦[/]",
        "[deepskyblue1]⠧[/]",
        "[deepskyblue1]⠇[/]",
        "[deepskyblue1]⠏[/]",
    ];

    public ChatLayout(ChatAgent chat, Rendering.ChatRenderer renderer, QuestionService? questionService = null,
        SessionManager? sessions = null)
    {
        _chat = chat;
        _renderer = renderer;
        _sessions = sessions ?? new SessionManager();
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

        LTAI.Agent.Tools.SubagentTools.OnSubagentComplete += (id) => _subagentProgress = -1;
        LTAI.Agent.Tools.SubagentTools.OnSubagentMessage += (id, role, content) =>
        {
            Interlocked.Increment(ref _subagentProgress);
        };
    }

    public async Task<TuiView?> RenderAsync()
    {
        LastRequestedView = TuiView.Chat;
        // 预热 LLM session + 初始化（在 Live 之前，用 Status 显示）
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

                    // 等待预热完成（最多 6 秒）
                    try { await warmupTask.WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false); }
                    catch { /* 预热超时不影响主流程 */ }

                    ctx.Status = "[green]✓ 初始化完成[/]";
                    await Task.Delay(200);
                }).ConfigureAwait(false);
        }
        catch { /* 非交互终端 */ }

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
                var cts = new CancellationTokenSource();

                var blinkCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                var blinkTask = Task.Run(async () =>
                {
                    while (!blinkCts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(400, blinkCts.Token).ConfigureAwait(false);
                        lock (_layout)
                        {
                            UpdateFooter("", "", IsInputEmpty() && showWatermark);
                            _liveCtx?.Refresh();
                        }
                    }
                }, blinkCts.Token);


                var inputTask = Task.Run(async () =>
                {
                    var keyDispatcher = new Input.KeyDispatcher(this);
                    while (!cts.Token.IsCancellationRequested)
                    {
                        var key = Console.ReadKey(true);
                        var keepGoing = await keyDispatcher.HandleKeyAsync(key, cts.Token).ConfigureAwait(false);
                        if (!keepGoing) { cts.Cancel(); return; }
                    }
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
        return LastRequestedView;
    }

    // ── 消息面板（Panel 包裹每条消息） ──

    private void UpdateHeader()
    {
        var planStatus = LTAI.Agent.Tools.PlanTools.PlanStatus();
        var hasPlan = planStatus.Contains("Current Step") || planStatus.Contains("executing");
        var planTag = hasPlan ? "  [bold yellow]📋 计划执行中[/]" : "";
        _layout["Header"].Update(
            new Panel($"[bold]LTAI 聊天[/]{planTag} — [grey]Esc=退出  Enter=发送  S+Enter=换行  1-5=视图  /help=帮助[/]")
                .Border(BoxBorder.None).Expand());
    }

    /// <summary>为一条消息构建 Spectre Panel，带颜色边框和 Header。</summary>
    private static Panel BuildMessagePanel(string role, string rawContent)
    {
        // ── 配色规范 ──
        //  User  → Cyan    Rounded
        //  AI    → Green   Double
        //  Tool  → Blue    Square
        //  Error → Red     Ascii
        //  其他  → Grey    None
        var (color, border, header) = (role.ToLowerInvariant()) switch
        {
            "user" => (Color.Cyan, BoxBorder.Rounded, "[bold cyan] 🧑 你 [/]"),
            "assistant" or "ai" => (Color.Green, BoxBorder.Double, "[bold green] 🤖 AI [/]"),
            "tool" => (Color.Blue, BoxBorder.Square, "[bold blue] 🔧 工具 [/]"),
            "error" => (Color.Red, BoxBorder.Ascii, "[bold red] ⛔ 错误 [/]"),
            "cmd" or "system" => (Color.Yellow, BoxBorder.Square, "[bold yellow] ⚙️ 系统 [/]"),
            _ => (Color.Grey, BoxBorder.None, "[bold grey] ℹ️ [/]"),
        };

        IRenderable content;
        if (role == "assistant" || role == "ai")
        {
            content = new Markup(MdToPanelContent(rawContent));
        }
        else if (role == "cmd" || role == "system" || role == "tool")
        {
            content = new Markup(rawContent);
        }
        else if (role == "user")
        {
            // 高亮用户输入的 /commands 和 #tags
            var highlighted = HighlightCommands(rawContent.EscapeMarkup());
            content = new Markup(highlighted);
        }
        else
        {
            content = new Markup(rawContent.EscapeMarkup());
        }

        return new Panel(
            Align.Left(content, VerticalAlignment.Top)
        )
        {
            Border = border,
            Header = new PanelHeader(header, Justify.Left),
            BorderStyle = new Style(color),
            Padding = new Padding(1, 0, 1, 0),
            Expand = true,
        };
    }

    /// <summary>构建一个包裹在 Panel 中的代码块。</summary>
    private static Panel BuildCodeBlockPanel(string code, string? lang)
    {
        return new Panel(
            Align.Left(
                new Markup($"[grey]{code.EscapeMarkup()}[/]"),
                VerticalAlignment.Top
            ))
        {
            Border = BoxBorder.Heavy,
            BorderStyle = new Style(Color.Grey42),
            Header = new PanelHeader(
                $"[bold grey] {(lang ?? "code").EscapeMarkup()} [/]", Justify.Left),
            Padding = new Padding(2, 0, 2, 0),
            Expand = true,
        };
    }

    private void UpdateMessages(string streamingContent)
    {
        var allMessages = new List<IRenderable>();

        // 渲染每条历史消息为独立 Panel
        for (int i = 0; i < _history.Count; i++)
        {
            var (role, rendered, rawContent, _) = _history[i];
            Panel panel;
            if (rendered != null && rendered is Panel p)
            {
                panel = p;
            }
            else
            {
                panel = _renderer.BuildMessagePanel(role, rawContent, i, _history[i].reasoning, _expandedMessages);
                _history[i] = (role, panel, rawContent, _history[i].reasoning);
            }
            allMessages.Add(panel);
        }

        // 流式内容：实时构建 AI 响应 Panel
        if (!string.IsNullOrEmpty(streamingContent))
        {
            // 工具调用 Tree + AI 内容
            var combined = new StringBuilder();
            if (_toolCalls.Count > 0)
                combined.AppendLine(_renderer.RenderToolCallsAsTree(_toolCalls));
            combined.Append(_renderer.MdToPanelContent(streamingContent));
            var rendered = combined.ToString().TrimEnd();
            var content = rendered.Length > 0
                ? (IRenderable)new Markup(rendered)
                : new Markup("[grey]等待 AI 回复...[/]");
            var streamPanel = new Panel(
                Align.Left(content, VerticalAlignment.Top))
            {
                Border = BoxBorder.Double,
                Header = new PanelHeader("[bold green] 🤖 AI 回复中... [/]", Justify.Left),
                BorderStyle = new Style(Color.Green),
                Padding = new Padding(1, 0, 1, 0),
                Expand = true,
            };
            allMessages.Add(streamPanel);
        }

        // 空状态 → 欢迎屏
        if (allMessages.Count == 0)
        {
            RestoreMessagesPanel();
            return;
        }

        // ── Viewport 裁剪：scrollOffset 控制可见范围 ──
        var messages = new List<IRenderable>();
        int totalMessages = allMessages.Count;
        int visibleCount = Math.Min(_maxVisibleMessages, totalMessages);
        // scrollOffset = 0 表示最新消息可见；>0 则向上偏移
        int startIdx = Math.Max(0, totalMessages - visibleCount - _scrollOffset);
        int endIdx = Math.Min(totalMessages, startIdx + _maxVisibleMessages);
        // 如果 scrollOffset 推到顶部，固定从 0 开始
        if (startIdx < 0) { _scrollOffset += startIdx; startIdx = 0; }

        if (startIdx > 0)
        {
            messages.Add(new Markup(
                $"[dim]↕ 以上 {startIdx} 条消息已滚动  Shift+↑↓/PgUp/PgDn 翻页  (共 {totalMessages} 条)[/]"));
        }
        messages.AddRange(allMessages.GetRange(startIdx, endIdx - startIdx));

        // 底部空白提示
        if (_scrollOffset > 0 && endIdx < totalMessages)
        {
            messages.Add(new Markup(
                $"[dim]↓ 还有 {totalMessages - endIdx} 条消息在后面  按 Shift+↓ 或 PgDn 滚动[/]"));
        }

        _layout["Messages"].Update(
            new Panel(new Rows(messages))
                .Border(BoxBorder.None)
                .Expand());
    }

    private void RestoreMessagesPanel()
    {
        if (_history.Count > 0)
        {
            UpdateMessages("");
            return;
        }
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
    }

    /// <summary>高亮用户输入中的 /commands 和 #tags。</summary>
    private static string HighlightCommands(string escaped)
    {
        // /command 开头的词 → 黄色高亮
        escaped = Regex.Replace(escaped, @"(^|\s)(/[a-zA-Z][\w-]*)", m =>
            m.Groups[1].Value + "[bold yellow]" + m.Groups[2].Value + "[/]");
        // #tag → 青色
        escaped = Regex.Replace(escaped, @"(^|\s)(#[\w-]+)", m =>
            m.Groups[1].Value + "[bold cyan]" + m.Groups[2].Value + "[/]");
        return escaped;
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

    private static string LongestCommonPrefix(List<string> strings)
    {
        if (strings.Count == 0) return "";
        if (strings.Count == 1) return strings[0];

        var first = strings[0];
        for (int i = 0; i < first.Length; i++)
        {
            for (int j = 1; j < strings.Count; j++)
            {
                if (i >= strings[j].Length || strings[j][i] != first[i])
                    return first[..i];
            }
        }
        return first;
    }

    // ── Footer ──

    private void UpdateFooter(string pickerText, string statusText, bool isFirstEmpty = false,
        List<SlashCommands.SuggestionItem>? suggestions = null, int selIdx = -1)
    {
        var renders = new List<IRenderable>();
        var r = UsageTracker.Requests;

        if (r > 0)
        {
            var m = UsageTracker.ActiveModel.EscapeMarkup();
            var b = UsageTracker.BalanceDisplay.EscapeMarkup();
            var tps = UsageTracker.TpsDisplay;
            var tc = UsageTracker.ToolCalls;
            var saved = UsageTracker.CacheSavedDisplay;

            // 第1行：模型 · Token · 费用 · 请求 · 速率
            var l1 = new Markup(
                $"[bold]{m}[/]  [grey]·[/]  Token: {UsageTracker.TotalTokens:N0}" +
                $"  [grey]·[/]  费用: {UsageTracker.CostDisplay.EscapeMarkup()}" +
                (string.IsNullOrEmpty(tps) ? "" : $"  [grey]·[/]  {tps}") +
                $"  [grey]·[/]  请求: {r}");
            renders.Add(l1);

            // 第2行：余额 · 缓存命中 · 工具调用 · 缓存节省
            var l2 = new Markup(
                $"余额: {b}  [grey]·[/]  缓存: {UsageTracker.CacheHitRate:F1}%" +
                (tc > 0 ? $"  [grey]·[/]  工具: {tc}次" : "") +
                (saved != "¥0.0000" ? $"  [grey]·[/]  节省: {saved}" : ""));
            renders.Add(l2);
        }
        else
        {
            var msg = _startupMessage;
            if (msg != null)
            {
                renders.Add(new Markup($"[yellow]⚠️ {msg.EscapeMarkup()}[/]"));
                renders.Add(new Markup("[grey]等待首次请求...  输入消息开始对话[/]"));
                _startupMessage = null; // show once
            }
            else
            {
                renders.Add(new Markup("[grey]等待首次请求...  输入消息开始对话[/]"));
            }
        }

        // 状态行（思考中/处理中/错误）
        if (!string.IsNullOrEmpty(statusText))
            renders.Add(new Markup(statusText));

        // ── 多行输入区：最多 MaxInputLines 行 ──
        if (!string.IsNullOrEmpty(pickerText))
        {
            // 选择器模式：输入行 + 内联建议
            var cursorBlink = Environment.TickCount % 1000 < 530;
            var cursor = cursorBlink ? "[bold deepskyblue1]▌[/]" : " ";
            renders.Add(new Markup($"{cursor} {pickerText.EscapeMarkup()}"));

            // 内联建议列表
            if (suggestions != null && suggestions.Count > 0)
            {
                var displayed = suggestions.Take(6).ToList();
                var suggestionText = new StringBuilder();
                for (int i = 0; i < displayed.Count; i++)
                {
                    var s = displayed[i];
                    var isSelected = i == selIdx;
                    var cmd = s.Completion;
                    var desc = s.DisplayText.Split("  ").LastOrDefault() ?? "";
                    if (isSelected)
                        suggestionText.Append($"[black on cyan] {cmd,-12} [/]");
                    else
                        suggestionText.Append($" [grey]{cmd,-12}[/]");
                }
                if (suggestions.Count > 6)
                    suggestionText.Append($" [dim]... +{suggestions.Count - 6}[/]");
                renders.Add(new Markup(suggestionText.ToString().TrimStart()));
                renders.Add(new Markup("[dim]↑↓=选择  Tab=补全  Enter=执行  Esc=取消[/]"));
            }
        }
        else
        {
            var showWatermark = IsInputEmpty() && isFirstEmpty;
            var cursorBlink = Environment.TickCount % 1000 < 530;
            var cursor = cursorBlink ? "[bold deepskyblue1]▌[/]" : " ";

            if (showWatermark)
            {
                renders.Add(new Markup(
                    $"{cursor} [dim]│[/][grey] 输入消息  SEnter=发送  Enter=换行  ↑↓=光标  /开命令  Ctrl+↑↓=历史  /[/]"));
            }
            else
            {
                // 找出包含光标行的可见行范围（最多 MaxInputLines 行）
                var visibleStart = Math.Max(0, _cursorLine - MaxInputLines + 1);
                var visibleLines = _inputLines
                    .Skip(visibleStart)
                    .Take(MaxInputLines)
                    .ToList();

                foreach (var (line, idx) in visibleLines.Select((l, i) => (l, i)))
                {
                    var lineNum = visibleStart + idx;
                    var isCursorLine = lineNum == _cursorLine;
                    var prefix = isCursorLine && cursorBlink ? "[bold deepskyblue1]▌[/]" :
                                 isCursorLine ? " [grey]▌[/]" : "  ";

                    var colored = _renderer.HighlightCommands(line.EscapeMarkup());
                    renders.Add(new Markup($"{prefix} {colored}"));
                }
            }
        }

        _layout["Footer"].Update(
            new Panel(new Rows(renders.ToArray()))
                .Border(BoxBorder.None)
                .Expand());
    }

    // ── Markdown → Spectre Panel 内容 ──

    private static string MdToPanelContent(string text)
    {
        var result = new StringBuilder();
        var inCodeBlock = false;
        var codeLines = new List<string>();
        var codeLang = "";

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```"))
            {
                if (inCodeBlock)
                {
                    // ── 渲染代码块为框线框 ──
                    var maxWidth = codeLines.Count > 0 ? codeLines.Max(l => l.Length) : 20;
                    var boxWidth = Math.Min(maxWidth + 4, Console.WindowWidth - 10);
                    var langLabel = string.IsNullOrEmpty(codeLang) ? "code" : codeLang;

                    // 顶框 ┌─ lang ────────────────────┐
                    var top = "┌─ " + langLabel + " " + new string('─', Math.Max(0, boxWidth - langLabel.Length - 3)) + "┐";
                    result.AppendLine($"[bold grey]{top}[/]");

                    // 内容行 │ code │
                    foreach (var cl in codeLines)
                    {
                        var padded = cl.Length <= boxWidth - 4
                            ? cl + new string(' ', boxWidth - 4 - cl.Length)
                            : cl[..(boxWidth - 7)] + "...";
                        result.AppendLine($"  [grey]│[/] {padded.EscapeMarkup()} [grey]│[/]");
                    }

                    // 底框 └───────────────────────────┘
                    var bottom = "└" + new string('─', boxWidth) + "┘";
                    result.AppendLine($"[bold grey]{bottom}[/]");

                    codeLines.Clear();
                    inCodeBlock = false;
                }
                else
                {
                    codeLang = trimmed[3..].Trim();
                    inCodeBlock = true;
                }
            }
            else if (inCodeBlock)
            {
                codeLines.Add(line);
            }
            else
            {
                var rendered = MdLineToSpectre(line);
                if (!string.IsNullOrEmpty(rendered))
                    result.AppendLine(rendered);
                else
                    result.AppendLine();
            }
        }

        // 未闭合的代码块（流式进行中）→ 用旧风格回退显示
        if (inCodeBlock)
        {
            result.AppendLine($"[grey]```{codeLang.EscapeMarkup()}[/]");
            foreach (var cl in codeLines)
                result.AppendLine($"  [grey]{cl.EscapeMarkup()}[/]");
        }

        return result.ToString().TrimEnd();
    }

    private static string MdLineToSpectre(string line)
    {
        var trimmed = line.TrimEnd();
        if (string.IsNullOrWhiteSpace(trimmed)) return "";

        string prefix = "";
        string suffix = "";
        string body = trimmed;

        if (trimmed.StartsWith("# "))
        { prefix = "[bold yellow]"; suffix = "[/]"; body = trimmed[2..]; }
        else if (trimmed.StartsWith("## "))
        { prefix = "[bold]"; suffix = "[/]"; body = trimmed[3..]; }
        else if (trimmed.StartsWith("### "))
        { prefix = "[bold cyan]"; suffix = "[/]"; body = trimmed[4..]; }
        else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
        { prefix = "  [green]•[/] "; body = trimmed[2..]; }
        else if (trimmed.StartsWith("1. ") || trimmed.StartsWith("2. ") || trimmed.StartsWith("3. "))
        { prefix = $"  [grey]{trimmed[..3]}[/]"; body = trimmed[3..]; }
        else if (trimmed.StartsWith("> "))
        { prefix = "  [grey]│[/] [italic]"; suffix = "[/]"; body = trimmed[2..]; }
        else if (trimmed.StartsWith("|") && trimmed.EndsWith("|"))
        {
            // 表格行：跳过分隔行（|---|），其余渲染为带边框的行
            if (Regex.IsMatch(trimmed, @"^\|[\s\-:]+\|$")) return "";
            var cells = trimmed.Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(c => c.Trim())
                .Select(c => InlineMdToSpectre(c));
            return "[grey]│[/] " + string.Join(" [grey]│[/] ", cells) + " [grey]│[/]";
        }

        var spectre = InlineMdToSpectre(body);
        return prefix + spectre + suffix;
    }

    private static readonly Regex InlineMdRx = new(
        @"\*\*(.+?)\*\*|__(.+?)__|\*(.+?)\*|_(.+?)_|``(.+?)``|`(.+?)`|\[\[(.+?)\]\]\((.+?)\)|~~(.+?)~~",
        RegexOptions.Compiled);

    private static readonly HashSet<string> _knownMarkupTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "bold", "italic", "grey", "cyan", "green", "yellow", "white", "black",
        "red", "blue", "aqua", "purple", "orange", "dim", "invert", "underline",
        "strikethrough", "/", "link"
    };

    private static string InlineMdToSpectre(string text)
    {
        // 先转义方括号，再合并为一次正则替换
        text = text.Replace("[", "[[").Replace("]", "]]");
        text = InlineMdRx.Replace(text, m =>
        {
            if (m.Groups[1].Success) return $"[bold]{m.Groups[1].Value}[/]";
            if (m.Groups[2].Success) return $"[bold]{m.Groups[2].Value}[/]";
            if (m.Groups[3].Success) return $"[italic]{m.Groups[3].Value}[/]";
            if (m.Groups[4].Success) return $"[italic]{m.Groups[4].Value}[/]";
            if (m.Groups[5].Success) return $"[grey]{m.Groups[5].Value}[/]";
            if (m.Groups[6].Success) return $"[grey]{m.Groups[6].Value}[/]";
            if (m.Groups[7].Success) return $"[link={m.Groups[8].Value}]{m.Groups[7].Value}[/]";
            if (m.Groups[9].Success) return $"[strikethrough]{m.Groups[9].Value}[/]";
            return m.Value;
        });
        return text;
    }

    /// <summary>将工具调用列表渲染为 Tree 样式文本。</summary>
    private static string RenderToolCallsAsTree(List<(string name, string args, string result)> calls)
    {
        var sb = new StringBuilder();
        foreach (var (name, args, result) in calls)
        {
            var a = Truncate(args, 40);
            sb.AppendLine($"[bold yellow]🔧 {name}[/]([grey]{a.EscapeMarkup()}[/])");
            var r = Truncate(result, 80);
            if (!string.IsNullOrEmpty(r))
                sb.AppendLine($"  [green]└─[/] {r.EscapeMarkup()}");
        }
        return sb.ToString().TrimEnd();
    }

    // ── 流式响应 ──

    private async Task StreamResponseAsync(string input)
    {
        var content = new StringBuilder();
        using var cts = new CancellationTokenSource();
        var sharedFrameIdx = 0;
        string statusText = "";

        // 初始等待提示
        content.AppendLine("━━━ 思考中 ━━━");
        _toolCallCount = 0;
        _toolCalls.Clear();

        // 上下文窗口自动 truncate：超过 75% 时触发压缩提示
        if (UsageTracker.ContextRatio() > 0.75)
        {
            var ctxPct = (UsageTracker.ContextRatio() * 100).ToString("F0");
            var msg = $"[dim]📐 上下文已使用 {ctxPct}%，自动压缩中...[/]";
            _history.Add(("cmd", null, msg, null));
            InvalidateRendered();
        }

        var toolTimer = Stopwatch.StartNew();
        UpdateFooter("", $"[deepskyblue1]{Rendering.ChatRenderer.PulseFrames[0]} 思考中...[/]");
        _liveCtx?.Refresh();

        // 后台脉冲动画（每 250ms 更新一次，即使无 token）
        using var spinCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var spinTask = Task.Run(async () =>
        {
            try
            {
                while (!spinCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(250, spinCts.Token).ConfigureAwait(false);
                        var idx = Interlocked.Increment(ref sharedFrameIdx);
                        var pulse = Rendering.ChatRenderer.PulseFrames[idx % Rendering.ChatRenderer.PulseFrames.Length];
                        var elapsed = toolTimer.Elapsed;
                        var timeStr = elapsed.TotalSeconds >= 60
                            ? $"{(int)elapsed.TotalMinutes}m{elapsed.Seconds}s"
                            : $"{elapsed.TotalSeconds:F1}s";
                        var line = $"{pulse} 思考中... [{timeStr}]";
                    if (!string.IsNullOrEmpty(statusText))
                        line += $"  {statusText}";
                    lock (_layout)
                    {
                        UpdateFooter("", $"[deepskyblue1]{line}[/]");
                        _liveCtx?.Refresh();
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"spinTask: {ex.Message}"); }
        }, spinCts.Token);

        try
        {
            var sessionHandle = _sessions.CurrentHandle;
            await foreach (var update in _chat.ChatStreamingAsync(input, sessionHandle).WithCancellation(cts.Token).ConfigureAwait(false))
            {
                if (cts.Token.IsCancellationRequested) break;

                var token = update.Text ?? "";
                if (string.IsNullOrEmpty(token))
                {
                    if (update.Contents?.Count > 0)
                    {
                        foreach (var c in update.Contents)
                        {
                            if (c is Microsoft.Extensions.AI.FunctionCallContent fc)
                            {
                                // P17.5: inline question form for ask_questions tool
                                if (string.Equals(fc.Name, "AskQuestions", StringComparison.Ordinal))
                                {
                                    var qp = _pendingQuestion;
                                    if (qp != null)
                                    {
                                        _pendingQuestion = null;
                                        await ShowQuestionFormAsync(qp, cts.Token).ConfigureAwait(false);
                                    }
                                }
                                _toolCallCount++;
                                var n = fc.Name ?? "";
                                var a = fc.Arguments is Dictionary<string, object?> args
                                    ? string.Join(", ", args.Select(kv => $"{kv.Key}={kv.Value}"))
                                    : "";
                                _toolCalls.Add((n, a, ""));

                                var elapsedStr = FormatElapsed(toolTimer.Elapsed);
                                statusText = $"🛠 {n}({Truncate(a, 30)}) [{elapsedStr}]";
                                if (n.Contains("SubmitPlan") || n.Contains("ApprovePlan") || n.Contains("StartExecution"))
                                    UpdateHeader();
                            }
                            // 显示工具调用结果
                            if (c is Microsoft.Extensions.AI.FunctionResultContent frc)
                            {
                                var resultStr = frc.Result?.ToString() ?? "";

                                // ── 工具确认请求拦截 ──
                                // 当工具返回 "⚠️ 需要确认..." 等模式时，
                                // 弹出模态窗口替代 LLM 往返确认流程。
                                if (TryParseConfirmRequest(resultStr, out var confirmInfo))
                                {
                                    var choice = ConfirmationModal.ShowInline(
                                        _layout, _liveCtx!,
                                        confirmInfo.Title,
                                        confirmInfo.Message,
                                        resultStr,
                                        confirmInfo.ExtraInfo);

                                    if (choice == ConfirmChoice.Always)
                                    {
                                        // 总是允许 → 自动重试（后续同类操作不再弹窗）
                                        content.AppendLine($"  ✅ [bold]已确认（本次会话始终允许）[/]");
                                        statusText = "✅ 已授权 (Always)";
                                    }
                                    else if (choice == ConfirmChoice.Yes)
                                    {
                                        content.AppendLine($"  ✅ [bold]已确认[/]");
                                        statusText = "✅ 已确认";
                                    }
                                    else if (choice == ConfirmChoice.No)
                                    {
                                        content.AppendLine($"  ⛔ [bold red]已拒绝[/]");
                                        statusText = "⛔ 已拒绝";
                                    }
                                    continue;
                                }

                                // ── 普通结果显示 ──
                                var displayResult = resultStr;
                                if (displayResult.Length > 300)
                                    displayResult = displayResult[..300] + "...";
                                // 更新最后一个工具调用的结果
                                if (_toolCalls.Count > 0)
                                    _toolCalls[^1] = (_toolCalls[^1].name, _toolCalls[^1].args, displayResult);
                            }
                        }
                    }
                    lock (_layout) { UpdateMessages(content.ToString()); UpdateFooter("", $"{Rendering.ChatRenderer.PulseFrames[sharedFrameIdx % Rendering.ChatRenderer.PulseFrames.Length]} 处理中...  {statusText}"); }
                    ThrottledRefresh();
                    continue;
                }
                if (TryParseToolResult(token, out var parsed))
                {
                    // 检测子 Agent 结果 → 显示耗时
                    var subMatch = System.Text.RegularExpressions.Regex.Match(token, @"\""type\"":\s*\""(\w+)\"".*\""spawnCount\"":\s*(\d+).*\""elapsedMs\"":\s*(\d+)");
                    if (subMatch.Success)
                    {
                        var st = subMatch.Groups[1].Value;
                        var sc = subMatch.Groups[2].Value;
                        var ms = int.Parse(subMatch.Groups[3].Value);
                        var timeStr = ms >= 1000 ? $"{ms / 1000}.{(ms % 1000) / 100}s" : $"{ms}ms";
                        var preview = Truncate(parsed.output.Replace("\n", " "), 50);
                        var msg = $"🔧 [bold]子任务 #{sc} ({st})[/] [grey]{timeStr}[/] — {preview.EscapeMarkup()}";
                        content.AppendLine(msg);
                        statusText = $"子任务 #{sc} 完成 ({timeStr})";
                    }
                    else
                    {
                        var msg = parsed.success
                            ? $"✅ {Truncate(parsed.output, 60)}"
                            : $"❌ {parsed.error.EscapeMarkup()}";
                        content.AppendLine(msg);
                        statusText = msg;
                    }
                    lock (_layout) { UpdateMessages(content.ToString()); UpdateFooter("", $"{Rendering.ChatRenderer.PulseFrames[sharedFrameIdx % Rendering.ChatRenderer.PulseFrames.Length]} 处理中...  {statusText}"); }
                    ThrottledRefresh();
                    continue;
                }
                if (token.StartsWith("HANDOFF TO "))
                {
                    content.AppendLine($"→ {token}"); statusText = $"→ {token}";
                    lock (_layout) { UpdateMessages(content.ToString()); UpdateFooter("", $"{Rendering.ChatRenderer.PulseFrames[sharedFrameIdx % Rendering.ChatRenderer.PulseFrames.Length]} {statusText}"); }
                    ThrottledRefresh();
                    continue;
                }
                if (token.StartsWith("[budget:") || token.StartsWith("[note:"))
                {
                    // Escape 方括号避免重渲时 Spectre MarkupException
                    var safeToken = token.Replace("[", "\\[").Replace("]", "\\]");
                    content.AppendLine(safeToken); statusText = token;
                    lock (_layout) { UpdateMessages(content.ToString()); UpdateFooter("", $"{Rendering.ChatRenderer.PulseFrames[sharedFrameIdx % Rendering.ChatRenderer.PulseFrames.Length]} {statusText}"); }
                    ThrottledRefresh();
                    continue;
                }

                content.Append(token);

                // 实时刷新：消息 + 动画
                lock (_layout) { UpdateMessages(content.ToString()); UpdateFooter("", $"{Rendering.ChatRenderer.PulseFrames[sharedFrameIdx % Rendering.ChatRenderer.PulseFrames.Length]} 处理中...  {statusText}"); }
                ThrottledRefresh();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            content.AppendLine($"\n[red]⚠ 流式响应错误: {ex.Message.EscapeMarkup()}[/]");
        }

        spinCts.Cancel();
        try { await spinTask.ConfigureAwait(false); } catch { /* 已取消或异常 */ }

        // 冻结：存储推理过程（可折叠）+ 最终回答
        var reasoning = _renderer.RenderToolCallsAsTree(_toolCalls);
        _history.Add(("assistant", null, content.ToString(), reasoning));
        _toolCalls.Clear();
        TrimHistory();
        SaveSession();
    }

    // ── Slash 命令 ──

    internal async Task<bool> HandleSlashCommandAsync(string input)
    {
        if (string.Equals(input, "/new", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(input, "/clear", StringComparison.OrdinalIgnoreCase))
        {
            SaveSession();
            _history.Clear();
            _toolCalls.Clear();
            _sessions.NewSession();
            return true;
        }
        if (input.StartsWith("/sessions", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("/session", StringComparison.OrdinalIgnoreCase))
        {
            HandleSessionsCommand(input);
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
                _history.Add(("cmd", null, cmdStatus, null));
            return running;
        }
        return true;
    }

    // ── 工具方法 ──

    private static bool TryParseToolResult(string text, out (bool success, string output, string error) result)
    {
        result = default;
        text = text.Trim();
        if (!text.StartsWith('{') || !text.EndsWith('}')) return false;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("success", out var s)) return false;
            var ok = s.GetBoolean();
            result = (ok, ok && root.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "",
                !ok && root.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "");
            return true;
        }
        catch { return false; }
    }

    /// <summary>解析后的工具确认请求信息。</summary>
    private sealed record ConfirmRequestInfo(
        string Title,
        string Message,
        string ExtraInfo);

    /// <summary>
    /// 检测工具返回值是否为确认请求。
    /// 识别模式：⚠️ 需要确认、需要用户确认、confirm、路径越界等。
    /// </summary>
    private static bool TryParseConfirmRequest(string text, out ConfirmRequestInfo info)
    {
        info = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;

        // ── Shell 命令确认 ──
        // 匹配: "⚠️ 需要执行 shell 命令，但尚未确认。\n命令: `xxx`\n目录: xxx"
        var shellMatch = Regex.Match(text,
            @"⚠️\s*需要.*(?:shell|命令).*确认.*\n命令:\s*`([^`]+)`.*\n目录:\s*(.+)",
            RegexOptions.Singleline);
        if (shellMatch.Success)
        {
            info = new ConfirmRequestInfo(
                "执行 Shell 命令",
                shellMatch.Groups[1].Value.Trim(),
                $"目录: {shellMatch.Groups[2].Value.Trim()}");
            return true;
        }

        // ── 路径访问确认（工作区外） ──
        // 匹配: "⚠️ 路径在工作区外: `xxx`" 或 "⚠️ 源路径在工作区外: `xxx`"
        var pathMatch = Regex.Match(text,
            @"⚠️.*路径在工作区外:\s*`([^`]+)`",
            RegexOptions.Singleline);
        if (pathMatch.Success)
        {
            info = new ConfirmRequestInfo(
                "访问工作区外路径",
                pathMatch.Groups[1].Value.Trim(),
                "路径在项目工作区之外，需要授权才能访问");
            return true;
        }

        // ── 文件下载确认 ──
        if (text.Contains("需要下载文件") && text.Contains("确认"))
        {
            // 尝试提取 URL
            var urlMatch = Regex.Match(text, @"https?://[^\s""'<>]+");
            var url = urlMatch.Success ? urlMatch.Value : "(未指定)";
            info = new ConfirmRequestInfo(
                "下载文件",
                url,
                "需要用户确认后才能下载外部文件");
            return true;
        }

        // ── 环境变量设置确认 ──
        if (text.Contains("设置环境变量") && text.Contains("确认"))
        {
            info = new ConfirmRequestInfo(
                "设置环境变量",
                "环境变量操作",
                "修改环境变量可能影响系统行为");
            return true;
        }

        // ── 编辑工作区外文件确认 ──
        var editMatch = Regex.Match(text,
            @"需要编辑工作区外的文件.*?目标路径:\s*`([^`]+)`",
            RegexOptions.Singleline);
        if (editMatch.Success)
        {
            info = new ConfirmRequestInfo(
                "编辑文件",
                editMatch.Groups[1].Value.Trim(),
                "文件在项目工作区之外");
            return true;
        }

        // ── 通用确认模式 ──
        if (text.Contains("⚠️") && text.Contains("确认"))
        {
            // 取第一行作为消息
            var firstLine = text.Split('\n')[0].Trim();
            info = new ConfirmRequestInfo(
                "安全确认",
                firstLine.Replace("⚠️", "").Trim(),
                "详情按 D 键查看");
            return true;
        }

        return false;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";

    private static string FormatElapsed(TimeSpan t) =>
        t.TotalSeconds >= 60 ? $"{(int)t.TotalMinutes}m{t.Seconds}s" : $"{t.TotalSeconds:F1}s";

    // ── P17.5 Inline Question Form ──

    private async Task ShowQuestionFormAsync(QuestionPost post, CancellationToken ct)
    {
        var answers = new List<IReadOnlyList<string>>();

        for (int i = 0; i < post.Questions.Count; i++)
        {
            var q = post.Questions[i];
            _pendingQuestion = null;
            var chosen = await ShowSingleQuestionAsync(q, i, post.Questions.Count, ct);
            answers.Add(chosen);
        }

        _questionService.Reply(post.RequestId, answers);
    }

    private async Task<IReadOnlyList<string>> ShowSingleQuestionAsync(QuestionPrompt q, int idx, int total, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"[yellow]── ❓ 问题 {idx + 1}/{total} ──[/]");
            sb.AppendLine($"[bold]{q.Header.EscapeMarkup()}[/]");
            sb.AppendLine($"[grey]{q.Question.EscapeMarkup()}[/]");
            sb.AppendLine();

            if (q.Options.Count > 0)
            {
                for (int j = 0; j < q.Options.Count; j++)
                {
                    var opt = q.Options[j];
                    var key = q.Multiple ? $"[{j + 1}]" : $"{(char)('a' + j)}";
                    sb.AppendLine($"  [cyan]{key}[/] {opt.Label.EscapeMarkup()}");
                    if (!string.IsNullOrEmpty(opt.Description))
                        sb.AppendLine($"     [dim]{opt.Description.EscapeMarkup()}[/]");
                }
                sb.AppendLine();
                sb.AppendLine(q.Multiple
                    ? "[grey]输入序号（逗号分隔多选, Enter 确认, c=自定义回答）: [/]"
                    : "[grey]输入字母选择 (a/b/c..., c=自定义, Enter 确认): [/]");
            }
            else
            {
                sb.AppendLine("[grey]输入回答 (Enter 确认): [/]");
            }

            lock (_layout)
            {
                _layout["Messages"].Update(new Panel(sb.ToString().TrimEnd()).Border(BoxBorder.Rounded).Expand());
                UpdateFooter("", $"[yellow]❓ 问题 {idx + 1}/{total}[/]");
                _liveCtx?.Refresh();
            }

            if (q.Options.Count > 0)
            {
                while (true)
                {
                    var key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.Enter)
                    {
                        UpdateFooter("", "[yellow]请选择一个选项[/]");
                        _liveCtx?.Refresh();
                        continue;
                    }

                    if (q.Multiple)
                    {
                        if (key.KeyChar == 'c' || key.KeyChar == 'C')
                            return new string[] { ShowTextInputInline(q, idx, total) };

                        var num = key.KeyChar - '0';
                        if (num >= 1 && num <= q.Options.Count)
                        {
                            var chosen = new List<string> { q.Options[num - 1].Label };
                            lock (_layout)
                            {
                                UpdateMessages($"[yellow]已选: {chosen[0]}. 按 Enter 确认或继续选择...[/]");
                                _liveCtx?.Refresh();
                            }
                            while (true)
                            {
                                var k2 = Console.ReadKey(true);
                                if (k2.Key == ConsoleKey.Enter) break;
                                if (k2.KeyChar == 'c' || k2.KeyChar == 'C')
                                {
                                    chosen.Clear();
                                    chosen.Add(ShowTextInputInline(q, idx, total));
                                    break;
                                }
                                var n2 = k2.KeyChar - '0';
                                if (n2 >= 1 && n2 <= q.Options.Count)
                                    chosen.Add(q.Options[n2 - 1].Label);
                            }
                            return chosen.ToArray();
                        }
                    }
                    else
                    {
                        var ch = char.ToLowerInvariant(key.KeyChar);
                        if (ch >= 'a' && ch < 'a' + q.Options.Count)
                        {
                            var selection = new string[] { q.Options[ch - 'a'].Label };
                            lock (_layout)
                            {
                                UpdateMessages($"[yellow]已选: {selection[0]}[/]");
                                _liveCtx?.Refresh();
                            }
                            return selection;
                        }
                        if (ch == 'c')
                            return new string[] { ShowTextInputInline(q, idx, total) };
                    }
                }
            }

            return new string[] { ShowTextInputInline(q, idx, total) };
        }, ct);
    }

    private string ShowTextInputInline(QuestionPrompt q, int idx, int total)
    {
        lock (_layout)
        {
            UpdateFooter("", $"[yellow]✏️ 问题 {idx + 1}/{total}: {q.Header.EscapeMarkup()}[/]");
            _liveCtx?.Refresh();
        }
        var input = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter && input.Length > 0)
                return input.ToString();
            if (key.Key == ConsoleKey.Escape)
                return "(跳过)";
            if (key.Key == ConsoleKey.Backspace && input.Length > 0)
                input.Length--;
            else if (!char.IsControl(key.KeyChar))
                input.Append(key.KeyChar);
        }
    }

    // ── Session 持久化 ──

    private void SaveSession()
    {
        if (_sessions.CurrentHandle == null) return;
        _sessions.SaveSession();
    }

    private void HandleSessionsCommand(string input)
    {
        var parts = input.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
        var arg = parts.Length > 2 ? parts[2] : "";

        switch (sub)
        {
            case "list":
            case "ls":
            case "":
                var sessions = _sessions.ListSessions();
                if (sessions.Length == 0)
                {
                    _history.Add(("cmd", null, "[yellow]📋 暂无已保存的会话[/]", null));
                }
                else
                {
                    var lines = new List<string> { "[bold yellow]📋 已保存的会话:[/]" };
                    foreach (var s in sessions.Take(20))
                    {
                        var marker = s.Name == _sessions.CurrentSession ? " [green]← 当前[/]" : "";
                        lines.Add($"  [cyan]{s.DisplayName,-22}[/]{marker}");
                    }
                    if (sessions.Length > 20)
                        lines.Add($"[grey]  ... 还有 {sessions.Length - 20} 个[/]");
                    lines.Add("[dim]使用 /sessions load <name> 加载[/]");
                    _history.Add(("cmd", null, string.Join("\n", lines), null));
                }
                break;

            case "load":
                if (string.IsNullOrEmpty(arg))
                {
                    _history.Add(("cmd", null, "[yellow]用法: /sessions load <会话名>[/]", null));
                    return;
                }
                SaveSession(); // 先保存当前会话
                var handle = _sessions.LoadSession(arg);
                if (handle != null)
                {
                    _history.Clear();
                    _toolCalls.Clear();
                    foreach (var m in handle.Messages)
                    {
                        var role = m.Role == Microsoft.Extensions.AI.ChatRole.User ? "user" : "assistant";
                        _history.Add((role, null, m.Text ?? "", null));
                    }
                    _history.Add(("cmd", null, $"[green]✅ 已加载会话: {SessionManager.FormatSessionName(arg)}[/]", null));
                }
                else
                {
                    _history.Add(("cmd", null, $"[red]❌ 找不到会话 '{arg}'。使用 /sessions list 查看[/]", null));
                }
                break;

            case "delete":
            case "rm":
                if (string.IsNullOrEmpty(arg))
                {
                    _history.Add(("cmd", null, "[yellow]用法: /sessions delete <会话名>[/]", null));
                    return;
                }
                _sessions.DeleteSession(arg);
                _history.Add(("cmd", null, $"[green]✅ 已删除会话: {arg}[/]", null));
                break;

            default:
                _history.Add(("cmd", null, "[yellow]用法: /sessions list|load <name>|delete <name>[/]", null));
                break;
        }
    }
}
