using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LTAI.Agent;
using LTAI.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LTAI.TUI;

public sealed class ChatLayout
{
    private readonly ChatAgent _chat;
    private readonly List<(string role, string content)> _history = new();
    private readonly Layout _layout;
    private readonly Queue<ConsoleKeyInfo> _pendingKeys = new();
    public TuiView? LastRequestedView { get; private set; }
    private int _toolCallCount;
    private int _subagentProgress;
    private const int MaxHistory = 200;
    private const int RefreshIntervalMs = 33;
    private DateTime _lastRefresh = DateTime.MinValue;
    private readonly StringBuilder _cachedMessages = new();
    private int _cachedHistoryCount = 0;
    private int _lastStreamRenderLen = 0;

    private void ThrottledRefresh(LiveDisplayContext ctx)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastRefresh).TotalMilliseconds >= RefreshIntervalMs)
        {
            ctx.Refresh();
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

    public ChatLayout(ChatAgent chat)
    {
        _chat = chat;

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
                var inputBuf = new StringBuilder();
                var showWatermark = true; // 首次显示水印，之后不再出现

                while (true)
                {
                    // 刷新：消息 + footer（含 "> " 输入框）
                    lock (_layout) { UpdateMessages(""); UpdateFooter(inputBuf.ToString(), "", showWatermark); ctx.Refresh(); }
                    showWatermark = false; // 只显示一次水印

                    // 读按键（优先消化缓冲键）
                    ConsoleKeyInfo key;
                    lock (_pendingKeys)
                    {
                        if (_pendingKeys.Count > 0) { key = _pendingKeys.Dequeue(); }
                        else { key = Console.ReadKey(true); }
                    }

                    // ── 视图切换：仅在输入为空时生效（避免误触） ──
                    if (inputBuf.Length == 0 && (key.KeyChar == '1' || key.KeyChar == '3' || key.KeyChar == '4' || key.KeyChar == '5'))
                    {
                        LastRequestedView = key.KeyChar switch
                        {
                            '1' => TuiView.Dashboard, '3' => TuiView.LLMConfig,
                            '4' => TuiView.TextPad, '5' => TuiView.Skills, _ => TuiView.Chat
                        };
                        return;
                    }
                    if (key.Key == ConsoleKey.Escape || key.KeyChar == 'q' || key.KeyChar == 'Q')
                    { LastRequestedView = null; return; }

                    // Ctrl+V → 粘贴
                    if (key.Key == ConsoleKey.V && (key.Modifiers & ConsoleModifiers.Control) != 0)
                    {
                        try { inputBuf.Append(TextCopy.ClipboardService.GetText() ?? ""); }
                        catch { lock (_layout) { UpdateFooter(inputBuf.ToString(), "[red]粘贴失败: 剪贴板不可用[/]"); ctx.Refresh(); } }
                        continue;
                    }

                    if (key.Key == ConsoleKey.Enter)
                    {
                        // Shift+Enter → 换行
                        if ((key.Modifiers & ConsoleModifiers.Shift) != 0)
                        {
                            inputBuf.Append('\n');
                            continue;
                        }

                        // Enter（无修饰键）→ 提交
                        var input = inputBuf.ToString().Trim();
                        inputBuf.Clear();

                        if (string.IsNullOrEmpty(input))
                        {
                            // 空输入 → 无操作，不退出
                            lock (_layout) { UpdateMessages(""); UpdateFooter("", ""); ctx.Refresh(); }
                            continue;
                        }

                        if (input.StartsWith('/'))
                        {
                            if (await HandleSlashCommandAsync(input).ConfigureAwait(false))
                            {
                                // D61: /snippet use may set a pending fill — load it into the input buffer
                                var fill = SlashCommands.PendingSnippetFill;
                                if (!string.IsNullOrEmpty(fill))
                                {
                                    SlashCommands.PendingSnippetFill = null;
                                    inputBuf.Clear();
                                    inputBuf.Append(fill);
                                }
                                continue;
                            }
                            LastRequestedView = null; return;
                        }

                        _history.Add(("user", input));
                        TrimHistory();
                        lock (_layout) { UpdateMessages(""); UpdateFooter("", ""); ctx.Refresh(); }

                        ConfirmationModal.AuthorizePaths(_layout, ctx, input);
                        await StreamResponseAsync(ctx, input).ConfigureAwait(false);
                        continue;
                    }

                    if (key.Key == ConsoleKey.Backspace && inputBuf.Length > 0)
                    {
                        inputBuf.Length--;
                        continue;
                    }

                    if (!char.IsControl(key.KeyChar))
                    {
                        inputBuf.Append(key.KeyChar);
                        // 首字符为 / → 弹出命令选择器模态窗口
                        if (inputBuf.Length == 1 && inputBuf[0] == '/')
                        {
                            inputBuf.Clear();

                            // 在 Messages 面板中打开命令选择器
                            var cmd = CommandPickerModal.Show(_layout, ctx);
                            // 取消 → 继续（下一次循环刷新会恢复 Messages）
                            if (cmd == null) continue;

                            if (await HandleSlashCommandAsync(cmd).ConfigureAwait(false))
                            {
                                // D61: /snippet use may set a pending fill — load it into the input buffer
                                var fill = SlashCommands.PendingSnippetFill;
                                if (!string.IsNullOrEmpty(fill))
                                {
                                    SlashCommands.PendingSnippetFill = null;
                                    inputBuf.Clear();
                                    inputBuf.Append(fill);
                                }
                                continue;
                            }
                            LastRequestedView = null; return;
                        }
                    }
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
            // 首次空状态保留欢迎屏
            return;
        }
        if (!string.IsNullOrEmpty(streamingContent) && _history.Count > 0 && _history[^1].role == "user")
            result = $"[bold green]┃[/] {result}";

        _layout["Messages"].Update(
            new Panel(result)
                .Border(BoxBorder.None)
                .Expand());
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

    private async Task StreamResponseAsync(LiveDisplayContext ctx, string input)
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
        ctx.Refresh();

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
                        ctx.Refresh();
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
                // 内联检查 ESC（缓冲非 ESC 按键）
                if (Console.KeyAvailable)
                {
                    var k = Console.ReadKey(true);
                    if (k.Key == ConsoleKey.Escape) { cts.Cancel(); break; }
                    lock (_pendingKeys) { _pendingKeys.Enqueue(k); }
                }
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
                                        _layout, ctx,
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
                    ThrottledRefresh(ctx);
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
                    ThrottledRefresh(ctx);
                    continue;
                }
                if (token.StartsWith("HANDOFF TO "))
                {
                    content.AppendLine($"→ {token}"); statusText = $"→ {token}";
                    lock (_layout) { UpdateMessages(content.ToString()); UpdateFooter("", $"{PulseFrames[Interlocked.Increment(ref sharedFrameIdx) % PulseFrames.Length]} {statusText}"); }
                    ThrottledRefresh(ctx);
                    continue;
                }
                if (token.StartsWith("[budget:") || token.StartsWith("[note:"))
                {
                    // Escape 方括号避免重渲时 Spectre MarkupException
                    var safeToken = token.Replace("[", "\\[").Replace("]", "\\]");
                    content.AppendLine(safeToken); statusText = token;
                    lock (_layout) { UpdateMessages(content.ToString()); UpdateFooter("", $"{PulseFrames[Interlocked.Increment(ref sharedFrameIdx) % PulseFrames.Length]} {statusText}"); }
                    ThrottledRefresh(ctx);
                    continue;
                }

                content.Append(token);

                // 实时刷新：消息 + 动画
                lock (_layout) { UpdateMessages(content.ToString()); var pulse = PulseFrames[Interlocked.Increment(ref sharedFrameIdx) % PulseFrames.Length]; UpdateFooter("", $"{pulse} 处理中...  {statusText}"); }
                ThrottledRefresh(ctx);
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
                _history.Add(("assistant", cmdStatus));
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
}
