using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using LTAI.Agent;
using LTAI.Core.Configuration;
using LTAI.Agent.Tools;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LTAI.TUI;

public sealed class ChatLayout
{
    private readonly ChatAgent _chat;
    private readonly List<(string role, string content)> _history = new();
    private readonly Layout _layout;
    private readonly QuestionService _questionService;
    private volatile QuestionPost? _pendingQuestion;

    private readonly System.Threading.Channels.Channel<string> _messageQueue =
        System.Threading.Channels.Channel.CreateBounded<string>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    private bool _processing;
    private CancellationTokenSource? _responseCts;
    private volatile char _quickNav;
    // 选择器状态（由输入任务管理，主线程只读）
    private volatile bool _pickerActive;
    private readonly object _pickerLock = new();
    private string _pickerFilter = "";
    private List<SlashCommands.SuggestionItem> _pickerItems = new();
    private int _pickerSelectedIdx;
    private LiveDisplayContext? _liveCtx;
    public TuiView? LastRequestedView { get; private set; }
    private int _toolCallCount;
    private int _subagentProgress;
    private const int MaxHistory = 200;
    private const int RefreshIntervalMs = 33;
    private DateTime _lastRefresh = DateTime.MinValue;
    private readonly StringBuilder _cachedMessages = new();
    private int _cachedHistoryCount = 0;
    private int _lastStreamRenderLen = 0;

    private void ThrottledRefresh()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastRefresh).TotalMilliseconds >= RefreshIntervalMs && _liveCtx != null)
        {
            _liveCtx.Refresh();
            _lastRefresh = now;
        }
    }

    private void TrimHistory()
    {
        if (_history.Count <= MaxHistory) return;
        _history.RemoveRange(0, _history.Count - MaxHistory);
        _cachedMessages.Clear();
        _cachedHistoryCount = 0;
    }
    private static readonly string[] PulseFrames = [
        "[yellow]▁▂▃▄▅▆▇█▇▆▅▄▃▂▁[/]",
        "[yellow]▂▃▄▅▆▇█▇▆▅▄▃▂▁▁[/]",
        "[yellow]▃▄▅▆▇█▇▆▅▄▃▂▁▁▂[/]",
        "[yellow]▄▅▆▇█▇▆▅▄▃▂▁▁▂▃[/]",
        "[yellow]▅▆▇█▇▆▅▄▃▂▁▁▂▃▄[/]",
        "[yellow]▆▇█▇▆▅▄▃▂▁▁▂▃▄▅[/]",
        "[yellow]▇█▇▆▅▄▃▂▁▁▂▃▄▅▆[/]",
        "[yellow]█▇▆▅▄▃▂▁▁▂▃▄▅▆▇[/]",
        "[yellow]▇▆▅▄▃▂▁▁▂▃▄▅▆▇█[/]",
        "[yellow]▆▅▄▃▂▁▁▂▃▄▅▆▇█▇[/]",
        "[yellow]▅▄▃▂▁▁▂▃▄▅▆▇█▇▆[/]",
        "[yellow]▄▃▂▁▁▂▃▄▅▆▇█▇▆▅[/]",
        "[yellow]▃▂▁▁▂▃▄▅▆▇█▇▆▅▄[/]",
        "[yellow]▂▁▁▂▃▄▅▆▇█▇▆▅▄▃[/]",
        "[yellow]▁▁▂▃▄▅▆▇█▇▆▅▄▃▂[/]",
    ];

    public ChatLayout(ChatAgent chat, QuestionService? questionService = null)
    {
        _chat = chat;
        _questionService = questionService ?? new QuestionService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<QuestionService>.Instance);
        _questionService.QuestionPosted += post => _pendingQuestion = post;

        _layout = new Layout()
            .SplitRows(
                new Layout("Header").Size(2),
                new Layout("Messages"),
                new Layout("Footer").Size(6));

        _layout["Header"].Update(
            new Panel("[bold]LTAI 聊天[/] — [grey]Esc=退出  Enter=发送  Shift+Enter=换行  Ctrl+V=粘贴  1-5=视图  /help=帮助[/]")
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
        // 预热 LLM session（后台异步，不阻塞 UI 启动）
        var warmupTask = _chat.WarmUpAsync();
        _ = warmupTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
                System.Diagnostics.Debug.WriteLine($"WarmUp failed: {t.Exception?.InnerException?.Message}");
        }, TaskContinuationOptions.OnlyOnFaulted);

        await AnsiConsole.Live(_layout)
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .Cropping(VerticalOverflowCropping.Top)
            .StartAsync(async ctx =>
            {
                _liveCtx = ctx;
                var inputBuf = new StringBuilder();
                var showWatermark = true;
                _processing = false;

                // P17.5: background task that reads keys independently from
                // message processing. User can type the next message while the
                // LLM is still responding to the previous one.
                var cts = new CancellationTokenSource();

                var inputTask = Task.Run(async () =>
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        var key = Console.ReadKey(true);

                        // ── 选择器激活时，路由所有按键到选择器 ──
                        if (_pickerActive)
                        {
                            string? pickerResult = null;
                            bool pickerDone = false;

                            if (key.Key == ConsoleKey.UpArrow)
                            {
                                lock (_pickerLock)
                                {
                                    if (_pickerItems.Count > 0)
                                        _pickerSelectedIdx = (_pickerSelectedIdx - 1 + _pickerItems.Count) % _pickerItems.Count;
                                }
                            }
                            else if (key.Key == ConsoleKey.DownArrow)
                            {
                                lock (_pickerLock)
                                {
                                    if (_pickerItems.Count > 0)
                                        _pickerSelectedIdx = (_pickerSelectedIdx + 1) % _pickerItems.Count;
                                }
                            }
                            else if (key.Key == ConsoleKey.Enter)
                            {
                                lock (_pickerLock)
                                {
                                    if (_pickerSelectedIdx >= 0 && _pickerSelectedIdx < _pickerItems.Count)
                                        pickerResult = _pickerItems[_pickerSelectedIdx].Completion;
                                }
                                pickerDone = true;
                            }
                            else if (key.Key == ConsoleKey.Escape || key.KeyChar == 'q')
                            {
                                pickerDone = true;
                            }
                            else if (key.Key == ConsoleKey.Backspace)
                            {
                                lock (_pickerLock)
                                {
                                    if (_pickerFilter.Length > 0)
                                        _pickerFilter = _pickerFilter[..^1];
                                    UpdatePickerItems();
                                }
                            }
                            else if (key.Key == ConsoleKey.Tab)
                            {
                                lock (_pickerLock)
                                {
                                    var completions = _pickerItems
                                        .Select(s => s.Completion)
                                        .Distinct(StringComparer.OrdinalIgnoreCase)
                                        .ToList();
                                    if (completions.Count == 1)
                                    {
                                        pickerResult = completions[0] + " ";
                                        pickerDone = true;
                                    }
                                    else if (completions.Count > 1)
                                    {
                                        var lcp = LongestCommonPrefix(completions);
                                        if (lcp.Length > ("/" + _pickerFilter).Length)
                                            _pickerFilter = lcp.Length > 1 ? lcp[1..] : "";
                                        UpdatePickerItems();
                                    }
                                }
                            }
                            else if (key.Key == ConsoleKey.J && _pickerFilter.Length == 0)
                            {
                                lock (_pickerLock)
                                {
                                    if (_pickerItems.Count > 0)
                                        _pickerSelectedIdx = (_pickerSelectedIdx + 1) % _pickerItems.Count;
                                }
                            }
                            else if (key.Key == ConsoleKey.K && _pickerFilter.Length == 0)
                            {
                                lock (_pickerLock)
                                {
                                    if (_pickerItems.Count > 0)
                                        _pickerSelectedIdx = (_pickerSelectedIdx - 1 + _pickerItems.Count) % _pickerItems.Count;
                                }
                            }
                            else if (!char.IsControl(key.KeyChar))
                            {
                                lock (_pickerLock)
                                {
                                    _pickerFilter += key.KeyChar;
                                    UpdatePickerItems();
                                }
                            }

                            if (pickerDone)
                            {
                                lock (_pickerLock)
                                {
                                    _pickerActive = false;
                                    _pickerFilter = "";
                                    _pickerItems = new();
                                    _pickerSelectedIdx = -1;
                                }
                                lock (_layout) RestoreMessagesPanel();
                                lock (inputBuf) inputBuf.Clear();
                                if (pickerResult != null)
                                {
                                    var handled = await HandleSlashCommandAsync(pickerResult).ConfigureAwait(false);
                                    if (!handled) { cts.Cancel(); return; }
                                }
                            }
                            continue;
                        }

                        // ── 普通模式（不变） ──

                        // 视图切换（仅空输入时）
                        if (inputBuf.Length == 0 && "1345".Contains(key.KeyChar))
                        {
                            lock (_layout) { _quickNav = key.KeyChar; }
                            continue;
                        }
                        if (key.Key == ConsoleKey.Escape || key.KeyChar == 'q')
                        {
                            // Cancel current response if processing, or quit
                            if (_processing) { _responseCts?.Cancel(); continue; }
                            cts.Cancel(); return;
                        }

                        // Ctrl+V → 粘贴
                        if (key.Key == ConsoleKey.V && (key.Modifiers & ConsoleModifiers.Control) != 0)
                        {
                            try { lock (inputBuf) inputBuf.Append(TextCopy.ClipboardService.GetText() ?? ""); }
                            catch { }
                            continue;
                        }

                        if (key.Key == ConsoleKey.Enter)
                        {
                            if ((key.Modifiers & ConsoleModifiers.Shift) != 0)
                            { lock (inputBuf) inputBuf.Append('\n'); continue; }

                            string input;
                            lock (inputBuf) { input = inputBuf.ToString().Trim(); inputBuf.Clear(); }
                            if (string.IsNullOrEmpty(input)) continue;

                            // Slash commands bypass queue and run instantly.
                            if (input.StartsWith('/'))
                            {
                                var handled = await HandleSlashCommandAsync(input).ConfigureAwait(false);
                                if (!handled) { cts.Cancel(); return; }
                                continue;
                            }

                            lock (_history) _history.Add(("user", input));
                            TrimHistory();
                            await _messageQueue.Writer.WriteAsync(input, cts.Token).ConfigureAwait(false);
                            continue;
                        }

                        if (key.Key == ConsoleKey.Backspace)
                        { lock (inputBuf) { if (inputBuf.Length > 0) inputBuf.Length--; } continue; }

                        if (!char.IsControl(key.KeyChar))
                        {
                            bool triggerPicker;
                            lock (inputBuf) { inputBuf.Append(key.KeyChar); triggerPicker = inputBuf.Length == 1 && inputBuf[0] == '/'; }
                            if (triggerPicker)
                            {
                                lock (_pickerLock)
                                {
                                    _pickerActive = true;
                                    _pickerFilter = "";
                                    _pickerItems = SlashCommands.GetSuggestionItems("/");
                                    _pickerSelectedIdx = _pickerItems.Count > 0 ? 0 : -1;
                                }
                            }
                        }
                    }
                }, cts.Token);

                // Main rendering + processing loop: runs at ~30fps, processes
                // queued messages one at a time while keeping the UI responsive.
                while (!cts.Token.IsCancellationRequested)
                {
                    // Refresh UI — picker 模式直接渲染选择器，跳过 UpdateMessages 避免闪烁
                    string buf;
                    lock (inputBuf) buf = inputBuf.ToString();
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
                            _layout["Messages"].Update(CommandPickerModal.BuildPicker(filter, items, selIdx));
                            // 选择器模式下，底部输入框显示过滤文本而非原始 inputBuf
                            var footerBuf = string.IsNullOrEmpty(filter) ? "/" : "/" + filter;
                            UpdateFooter(footerBuf, "", showWatermark);
                            ctx.Refresh();
                        }
                    }
                    else
                    {
                        lock (_layout) { UpdateMessages(""); UpdateFooter(buf, "", showWatermark); ctx.Refresh(); }
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
                    await Task.Delay(16, cts.Token).ConfigureAwait(false);
                }
            });
        return LastRequestedView;
    }

    // ── 消息面板（增量渲染） ──

    private void UpdateHeader()
    {
        var planStatus = LTAI.Agent.Tools.PlanTools.PlanStatus();
        var hasPlan = planStatus.Contains("Current Step") || planStatus.Contains("executing");
        var planTag = hasPlan ? "  [bold yellow]📋 计划执行中[/]" : "";
        _layout["Header"].Update(
            new Panel($"[bold]LTAI 聊天[/]{planTag} — [grey]Esc=退出  Enter=发送  S+Enter=换行  1-5=视图  /help=帮助[/]")
                .Border(BoxBorder.None).Expand());
    }

    private void UpdateMessages(string streamingContent)
    {
        var sb = new StringBuilder();

        // 使用缓存的已渲染历史，避免每 token 重新处理
        if (_cachedMessages.Length > 0)
            sb.Append(_cachedMessages);

        // 渲染新增的历史条目
        for (int i = _cachedHistoryCount; i < _history.Count; i++)
        {
            var (role, content) = _history[i];
            if (role == "user")
                sb.AppendLine($"[bold cyan]┃[/] [cyan]你:[/] {content.EscapeMarkup()}");
            else if (role == "cmd")
                sb.AppendLine(content);
            else
                sb.AppendLine($"[bold green]┃[/] {MdToPanelContent(content)}");
        }

        // 更新历史缓存
        if (_cachedHistoryCount < _history.Count)
        {
            _cachedMessages.Clear();
            _cachedMessages.Append(sb);
            _cachedHistoryCount = _history.Count;
        }

        // 流式内容增量：只处理上次渲染后的新增部分
        if (!string.IsNullOrEmpty(streamingContent))
        {
            // 新会话重置跟踪
            if (streamingContent.Length < _lastStreamRenderLen)
                _lastStreamRenderLen = 0;

            if (streamingContent.Length > _lastStreamRenderLen)
            {
                var delta = streamingContent[_lastStreamRenderLen..];
                sb.Append(MdToPanelContent(delta));
                _lastStreamRenderLen = streamingContent.Length;
            }
        }
        else
        {
            _lastStreamRenderLen = 0;
        }

        var result = sb.ToString().TrimEnd();
        if (_history.Count == 0 && string.IsNullOrEmpty(streamingContent) && _cachedHistoryCount == 0)
        {
            // 首次空状态保留欢迎屏（如果被命令选择器覆盖则恢复）
            RestoreMessagesPanel();
            return;
        }
        if (!string.IsNullOrEmpty(streamingContent) && _history.Count > 0 && _history[^1].role == "user")
            result = $"[bold green]┃[/] {result}";

        // 防御性转义：AI 返回的文本中可能包含 [次]、[步骤1] 等未被转义的括号，
        // Spectre.Console 会尝试将其作为 markup tag 解析导致崩溃。
        // 仅放行已知的 Spectre 标记标签，其余全部转义。
        result = Regex.Replace(result, @"(?<!\[)\[([^\[\]]+?)\](?!\])", m =>
        {
            var inner = m.Groups[1].Value.Trim();
            if (inner == "/") return m.Value;
            if (inner.Length > 0 && inner.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .All(part => _knownMarkupTokens.Contains(part)))
                return m.Value;
            return "[[" + inner + "]]";
        });

        _layout["Messages"].Update(
            new Panel(result)
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

    // ── 选择器辅助 ──

    /// <summary>根据当前 <c>_pickerFilter</c> 重新计算 <c>_pickerItems</c>。</summary>
    /// <remarks>调用方必须持有 <c>_pickerLock</c>。</remarks>
    private void UpdatePickerItems()
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

    private void UpdateFooter(string inputText, string statusText, bool isFirstEmpty = false)
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

            // 第1行：模型 · Token · 费用 · 速率 · 请求
            var l1 = $"{m}  ·  [grey]Token:[/] {UsageTracker.TotalTokens:N0}  ·  " +
                     $"[grey]费用:[/] {UsageTracker.CostDisplay.EscapeMarkup()}";
            if (!string.IsNullOrEmpty(tps)) l1 += $"  ·  [grey]速率:[/] {tps}";
            l1 += $"  ·  [grey]请求:[/] {r}";
            renders.Add(new Markup(l1));

            // 第2行：余额 · 缓存 · 工具 · 节省（仅在有数据时显示）
            var l2 = $"[grey]余额:[/] {b}  ·  [grey]缓存:[/] {UsageTracker.CacheHitRate:F1}%";
            if (tc > 0) l2 += $"  ·  [grey]工具:[/] {tc}次";
            if (saved != "¥0.0000") l2 += $"  ·  [grey]节省:[/] {saved}";
            renders.Add(new Markup(l2));
        }
        else
        {
            renders.Add(new Markup("[grey]等待首次请求...  输入消息开始对话[/]"));
        }

        // 状态行
        if (!string.IsNullOrEmpty(statusText))
            renders.Add(new Markup(statusText));

        // 输入行：首次空时显示水印+光标，之后始终只显示光标
        var showWatermark = string.IsNullOrEmpty(inputText) && isFirstEmpty;
        var inputDisplay = showWatermark
            ? $"[bold green]▎[/] [dim]│[/][grey] 输入消息  Enter=发送  S+Enter=换行  Ctrl+V=粘贴  /[/]"
            : $"[bold green]▎[/] {inputText}[bold yellow]│[/]";
        renders.Add(new Markup(inputDisplay));

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

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```"))
            {
                if (inCodeBlock)
                {
                    result.AppendLine("[grey]```[/]");
                    inCodeBlock = false;
                }
                else
                {
                    var lang = trimmed[3..].Trim();
                    result.AppendLine($"[grey]```{lang.EscapeMarkup()}[/]");
                    inCodeBlock = true;
                }
            }
            else if (inCodeBlock)
            {
                result.AppendLine($"  [grey]{line.EscapeMarkup()}[/]");
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

    // ── 流式响应 ──

    private async Task StreamResponseAsync(string input)
    {
        var content = new StringBuilder();
        using var cts = new CancellationTokenSource();
        var sharedFrameIdx = 0;
        string statusText = "";

        // 初始等待提示
        content.AppendLine("[dim]━━━ 思考中 ━━━[/]");
        _toolCallCount = 0;
        var toolTimer = Stopwatch.StartNew();
        UpdateFooter("", $"[grey]{PulseFrames[0]} 思考中...[/]");
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
                        var pulse = PulseFrames[idx % PulseFrames.Length];
                        var elapsed = toolTimer.Elapsed;
                        var timeStr = elapsed.TotalSeconds >= 60
                            ? $"{(int)elapsed.TotalMinutes}m{elapsed.Seconds}s"
                            : $"{elapsed.TotalSeconds:F1}s";
                        var line = $"{pulse} 思考中... [{timeStr}]";
                    if (!string.IsNullOrEmpty(statusText))
                        line += $"  {statusText}";
                    lock (_layout)
                    {
                        UpdateFooter("", $"[grey]{line}[/]");
                        _liveCtx?.Refresh();
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"spinTask: {ex.Message}"); }
        }, spinCts.Token);

        try
        {
            await foreach (var update in _chat.ChatStreamingAsync(input).WithCancellation(cts.Token).ConfigureAwait(false))
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
                                var msg = $"🛠 [{_toolCallCount}] {n}({Truncate(a, 50)})";
                                content.AppendLine(msg);
                                var elapsedStr = FormatElapsed(toolTimer.Elapsed);
                                statusText = $"{msg} [{elapsedStr}]";
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
                                content.AppendLine($"  📄 结果: {displayResult}");
                            }
                        }
                    }
                    lock (_layout) { UpdateMessages(content.ToString()); UpdateFooter("", $"{PulseFrames[Interlocked.Increment(ref sharedFrameIdx) % PulseFrames.Length]} 处理中...  {statusText}"); }
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
                    lock (_layout) { UpdateMessages(content.ToString()); UpdateFooter("", $"{PulseFrames[Interlocked.Increment(ref sharedFrameIdx) % PulseFrames.Length]} 处理中...  {statusText}"); }
                    ThrottledRefresh();
                    continue;
                }
                if (token.StartsWith("HANDOFF TO "))
                {
                    content.AppendLine($"→ {token}"); statusText = $"→ {token}";
                    lock (_layout) { UpdateMessages(content.ToString()); UpdateFooter("", $"{PulseFrames[Interlocked.Increment(ref sharedFrameIdx) % PulseFrames.Length]} {statusText}"); }
                    ThrottledRefresh();
                    continue;
                }
                if (token.StartsWith("[budget:") || token.StartsWith("[note:"))
                {
                    // Escape 方括号避免重渲时 Spectre MarkupException
                    var safeToken = token.Replace("[", "\\[").Replace("]", "\\]");
                    content.AppendLine(safeToken); statusText = token;
                    lock (_layout) { UpdateMessages(content.ToString()); UpdateFooter("", $"{PulseFrames[Interlocked.Increment(ref sharedFrameIdx) % PulseFrames.Length]} {statusText}"); }
                    ThrottledRefresh();
                    continue;
                }

                content.Append(token);

                // 实时刷新：消息 + 动画
                lock (_layout) { UpdateMessages(content.ToString()); var pulse = PulseFrames[Interlocked.Increment(ref sharedFrameIdx) % PulseFrames.Length]; UpdateFooter("", $"{pulse} 处理中...  {statusText}"); }
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

        _history.Add(("assistant", content.ToString()));
        TrimHistory();
    }

    // ── Slash 命令 ──

    private async Task<bool> HandleSlashCommandAsync(string input)
    {
        if (string.Equals(input, "/new", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(input, "/clear", StringComparison.OrdinalIgnoreCase))
        {
            _history.Clear();
            _cachedMessages.Clear();
            _cachedHistoryCount = 0;
            _lastStreamRenderLen = 0;
            return true;
        }
        if (string.Equals(input, "/exit", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(input, "/quit", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var cmdStatus = "";
        var running = true;
        if (SlashCommands.TryExecute(input, ref running, ref cmdStatus))
        {
            if (!string.IsNullOrEmpty(cmdStatus))
                _history.Add(("cmd", cmdStatus));
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
}
