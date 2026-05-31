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
    private static readonly string[] PulseFrames = [
        "[yellow]█[/].......",
        ".[yellow]█[/]......",
        "..[yellow]█[/].....",
        "...[yellow]█[/]....",
        "....[yellow]█[/]...",
        ".....[yellow]█[/]..",
        "......[yellow]█[/].",
        ".......[yellow]█[/]",
        "......[yellow]█[/].",
        ".....[yellow]█[/]..",
        "....[yellow]█[/]...",
        "...[yellow]█[/]....",
        "..[yellow]█[/].....",
        ".[yellow]█[/]......",
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
            new Panel("[bold]LTAI 聊天[/] — [grey]ESC 退出，Enter 发送，Shift+Enter 换行，/ 命令[/]")
                .Border(BoxBorder.None).Expand());

        _layout["Footer"].Update(
            new Panel("[grey]等待首次请求...  输入消息开始对话[/]")
                .Border(BoxBorder.None).Expand());
    }

    public async Task RenderAsync()
    {
        // 预热 LLM session（后台异步，不阻塞 UI 启动）
        var warmupTask = _chat.WarmUpAsync();

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
                    UpdateMessages("");
                    UpdateFooter(inputBuf.ToString(), "", showWatermark);
                    ctx.Refresh();
                    showWatermark = false; // 只显示一次水印

                    // 读按键（阻塞）
                    var key = Console.ReadKey(true);

                    if (key.Key == ConsoleKey.Escape) return;

                    // Ctrl+V → 粘贴
                    if (key.Key == ConsoleKey.V && (key.Modifiers & ConsoleModifiers.Control) != 0)
                    {
                        try { inputBuf.Append(TextCopy.ClipboardService.GetText() ?? ""); }
                        catch { }
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
                            UpdateMessages("");
                            UpdateFooter("", "");
                            ctx.Refresh();
                            return;
                        }

                        if (input.StartsWith('/'))
                        {
                            if (await HandleSlashCommandAsync(input)) continue;
                            return;
                        }

                        _history.Add(("user", input));
                        UpdateMessages("");
                        UpdateFooter("", "");
                        ctx.Refresh();

                        PreAuthorizePaths(input);
                        await StreamResponseAsync(ctx, input);
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

                            if (await HandleSlashCommandAsync(cmd)) continue;
                            return;
                        }
                    }
                }
            });
    }

    // ── 消息面板 ──

    private void UpdateMessages(string streamingContent)
    {
        var sb = new StringBuilder();
        foreach (var (role, content) in _history)
        {
            if (role == "user")
                sb.AppendLine($"[cyan]你:[/] {content.EscapeMarkup()}");
            else
                sb.AppendLine(MdToPanelContent(content));
        }
        if (!string.IsNullOrEmpty(streamingContent))
            sb.Append(MdToPanelContent(streamingContent));

        _layout["Messages"].Update(
            new Panel(sb.ToString().TrimEnd())
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
            var c = UsageTracker.ContextText();
            var pct = UsageTracker.ContextRatio();
            var b = UsageTracker.BalanceDisplay.EscapeMarkup();
            var tps = UsageTracker.TpsDisplay;
            var tc = UsageTracker.ToolCalls;
            var saved = UsageTracker.CacheSavedDisplay;

            // 第1行：模型 | Token | 费用 | 速率 | 请求
            var l1 = $"[grey]模型:[/] {m}  " +
                     $"[grey]Token:[/] {UsageTracker.TotalTokens:N0}  " +
                     $"[grey]费用:[/] {UsageTracker.CostDisplay.EscapeMarkup()}";
            if (!string.IsNullOrEmpty(tps)) l1 += $"  [grey]速率:[/] {tps}";
            l1 += $"  [grey]请求:[/] {r}";
            renders.Add(new Markup(l1));

            // 上下文进度（ContextText 已包含百分比）
            var ctxPctStr = $"[grey]上下文:[/] {c}";
            renders.Add(new Markup(ctxPctStr));

            // 第2行：余额 | 缓存% | 节省 | 工具
            var l2 = $"[grey]余额:[/] {b}  [grey]缓存:[/] {UsageTracker.CacheHitRate:F1}%";
            if (saved != "¥0.0000") l2 += $"  [grey]节省:[/] {saved}";
            if (tc > 0) l2 += $"  [grey]工具:[/] {tc}次";
            renders.Add(new Markup(l2));

            // 计时行：LLM 调用耗时 | 工具调用耗时
            var llmTime = UsageTracker.LlmCallTimeDisplay;
            var toolTime = UsageTracker.ToolCallTimeDisplay;
            if (!string.IsNullOrEmpty(llmTime) || !string.IsNullOrEmpty(toolTime))
            {
                var timingParts = new List<string>();
                if (!string.IsNullOrEmpty(llmTime))
                    timingParts.Add($"[grey]LLM:[/] {llmTime}");
                if (!string.IsNullOrEmpty(toolTime))
                    timingParts.Add($"[grey]工具:[/] {toolTime}");
                renders.Add(new Markup(string.Join("  ", timingParts)));
            }
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
            ? $"[bold green]❯[/] [bold yellow]█[/][grey] 输入消息 Enter发送 Shift+Enter换行 Ctrl+V粘贴 /new /exit[/]"
            : $"[bold green]❯[/] {inputText}[bold yellow]█[/]";
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
                .Select(c => InlineMdToSpectre(c.EscapeMarkup()));
            return "[grey]│[/] " + string.Join(" [grey]│[/] ", cells) + " [grey]│[/]";
        }

        var spectre = InlineMdToSpectre(body.EscapeMarkup());
        return prefix + spectre + suffix;
    }

    private static string InlineMdToSpectre(string text)
    {
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", m => $"[bold]{m.Groups[1].Value}[/]");
        text = Regex.Replace(text, @"__(.+?)__", m => $"[bold]{m.Groups[1].Value}[/]");
        text = Regex.Replace(text, @"\*(.+?)\*", m => $"[italic]{m.Groups[1].Value}[/]");
        text = Regex.Replace(text, @"_(.+?)_", m => $"[italic]{m.Groups[1].Value}[/]");
        text = Regex.Replace(text, @"``(.+?)``|`(.+?)`",
            m => $"[grey]{(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)}[/]");
        text = Regex.Replace(text, @"\[(.+?)\]\((.+?)\)", m => $"[link={m.Groups[2].Value}]{m.Groups[1].Value}[/]");
        text = Regex.Replace(text, @"~~(.+?)~~", m => $"[strikethrough]{m.Groups[1].Value}[/]");
        return text;
    }

    // ── 流式响应 ──

    private async Task StreamResponseAsync(LiveDisplayContext ctx, string input)
    {
        var content = new StringBuilder();
        var cts = new CancellationTokenSource();
        int frameIdx = 0;
        string statusText = "";

        // 后台 ESC 监控
        var escTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    if (Console.KeyAvailable)
                    {
                        var k = Console.ReadKey(true);
                        if (k.Key == ConsoleKey.Escape) { cts.Cancel(); return; }
                    }
                    await Task.Delay(100, cts.Token);
                }
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // 初始等待提示
        frameIdx = 0;
        UpdateFooter("", $"[grey]{PulseFrames[0]} 思考中...[/]");
        ctx.Refresh();

        // 后台脉冲动画（每 250ms 更新一次，即使无 token）
        var spinCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var spinTask = Task.Run(async () =>
        {
            try
            {
                while (!spinCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(250, spinCts.Token).ConfigureAwait(false);
                    var pulse = PulseFrames[frameIdx++ % PulseFrames.Length];
                    var line = $"{pulse} 思考中...";
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
        }, spinCts.Token);

        try
        {
            await foreach (var update in _chat.ChatStreamingAsync(input).WithCancellation(cts.Token))
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
                                var n = fc.Name ?? "";
                                var a = fc.Arguments is Dictionary<string, object?> args
                                    ? string.Join(", ", args.Select(kv => $"{kv.Key}={kv.Value}"))
                                    : "";
                                var msg = $"🛠 调用 {n}({Truncate(a, 50)})";
                                content.AppendLine(msg);
                                statusText = msg;
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
                                    var choice = ConfirmationModal.Show(
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
                    UpdateMessages(content.ToString());
                    UpdateFooter("", $"{PulseFrames[frameIdx++ % PulseFrames.Length]} 处理中...  {statusText}");
                    ctx.Refresh();
                    continue;
                }
                if (TryParseToolResult(token, out var parsed))
                {
                    var msg = parsed.success
                        ? $"✅ {Truncate(parsed.output, 60)}"
                        : $"❌ {parsed.error.EscapeMarkup()}";
                    content.AppendLine(msg);
                    statusText = msg;
                    UpdateMessages(content.ToString());
                    UpdateFooter("", $"{PulseFrames[frameIdx++ % PulseFrames.Length]} 处理中...  {statusText}");
                    ctx.Refresh();
                    continue;
                }
                if (token.StartsWith("HANDOFF TO "))
                {
                    content.AppendLine($"→ {token}"); statusText = $"→ {token}";
                    UpdateMessages(content.ToString());
                    UpdateFooter("", $"{PulseFrames[frameIdx++ % PulseFrames.Length]} {statusText}");
                    ctx.Refresh();
                    continue;
                }
                if (token.StartsWith("[budget:") || token.StartsWith("[note:"))
                {
                    content.AppendLine(token); statusText = token;
                    UpdateMessages(content.ToString());
                    UpdateFooter("", $"{PulseFrames[frameIdx++ % PulseFrames.Length]} {statusText}");
                    ctx.Refresh();
                    continue;
                }

                content.Append(token);

                // 实时刷新：消息 + 动画
                UpdateMessages(content.ToString());
                var pulse = PulseFrames[frameIdx++ % PulseFrames.Length];
                UpdateFooter("", $"{pulse} 处理中...  {statusText}");
                ctx.Refresh();
            }
        }
        catch (OperationCanceledException) { }

        cts.Cancel();
        spinCts.Cancel();
        try { await spinTask; } catch { }
        try { await escTask; } catch { }

        _history.Add(("assistant", content.ToString()));
    }

    // ── Slash 命令 ──

    private async Task<bool> HandleSlashCommandAsync(string input)
    {
        if (string.Equals(input, "/new", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(input, "/clear", StringComparison.OrdinalIgnoreCase))
        {
            _history.Clear();
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

    private static void PreAuthorizePaths(string input)
    {
        ConfirmationModal.AuthorizePaths(input);
    }
}
