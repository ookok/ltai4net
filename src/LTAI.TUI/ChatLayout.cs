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
    private static readonly string[] DotFrames = ["⚪", "⚫", "⚪"];

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

                    // Ctrl+V → 粘贴剪贴板内容
                    if (key.Key == ConsoleKey.V && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                    {
                        try { inputBuf.Append(TextCopy.ClipboardService.GetText() ?? ""); }
                        catch { }
                        continue;
                    }

                    if (key.Key == ConsoleKey.Enter)
                    {
                        // Shift+Enter → 换行
                        if (key.Modifiers.HasFlag(ConsoleModifiers.Shift))
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
                        // 删除上一个字符（\n 占 1 个 char）
                        inputBuf.Length--;
                        continue;
                    }

                    if (!char.IsControl(key.KeyChar))
                    {
                        inputBuf.Append(key.KeyChar);
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
            ? $"> [yellow]█[/][grey] 输入消息 Enter发送 Shift+Enter换行 Ctrl+V粘贴 /new /exit[/]"
            : $"> {inputText}[yellow]█[/]";
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
        var blocks = text.Split("```", StringSplitOptions.None);

        for (int bi = 0; bi < blocks.Length; bi++)
        {
            if (bi % 2 == 1)
            {
                // 代码块
                var lines = blocks[bi].Split('\n');
                var lang = lines[0].Trim();
                // 标题行
                result.AppendLine($"[grey]```{lang.EscapeMarkup()}[/]");
                for (int ln = 1; ln < lines.Length; ln++)
                    result.AppendLine($"  [grey]{lines[ln].EscapeMarkup()}[/]");
                result.AppendLine("[grey]```[/]");
            }
            else if (!string.IsNullOrWhiteSpace(blocks[bi]))
            {
                // 文本块
                foreach (var line in blocks[bi].Split('\n'))
                    result.AppendLine(MdLineToSpectre(line));
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
        UpdateFooter("", "[grey]⚪ 思考中...[/]");
        ctx.Refresh();

        // 后台动画（每 250ms 旋转一次，即使无 token）
        var spinCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var spinTask = Task.Run(async () =>
        {
            try
            {
                while (!spinCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(250, spinCts.Token).ConfigureAwait(false);
                    var dot = DotFrames[frameIdx++ % 3];
                    var line = $"{dot} 思考中...";
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
                                if (resultStr.Length > 300)
                                    resultStr = resultStr[..300] + "...";
                                // 只显示前 300 字符的结果预览，太长的结果用标记代替
                                content.AppendLine($"  📄 结果: {resultStr}");
                            }
                        }
                    }
                    UpdateMessages(content.ToString());
                    var dot = DotFrames[frameIdx++ % 3];
                    UpdateFooter("", $"{dot} 处理中...  {statusText}");
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
                    var dot = DotFrames[frameIdx++ % 3];
                    UpdateFooter("", $"{dot} 处理中...  {statusText}");
                    ctx.Refresh();
                    continue;
                }
                if (token.StartsWith("HANDOFF TO "))
                {
                    content.AppendLine($"→ {token}"); statusText = $"→ {token}";
                    UpdateMessages(content.ToString());
                    UpdateFooter("", $"{DotFrames[frameIdx++ % 3]} {statusText}");
                    ctx.Refresh();
                    continue;
                }
                if (token.StartsWith("[budget:") || token.StartsWith("[note:"))
                {
                    content.AppendLine(token); statusText = token;
                    UpdateMessages(content.ToString());
                    UpdateFooter("", $"{DotFrames[frameIdx++ % 3]} {statusText}");
                    ctx.Refresh();
                    continue;
                }

                content.Append(token);

                // 实时刷新：消息 + 动画
                UpdateMessages(content.ToString());
                var dots = DotFrames[frameIdx++ % 3];
                UpdateFooter("", $"{dots} 处理中...  {statusText}");
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

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";

    private static void PreAuthorizePaths(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return;

        var ws = Directory.GetCurrentDirectory();
        // 匹配 Windows 路径（支持中文、全角符号）和 Unix 路径
        var pathMatches = Regex.Matches(input,
            @"[A-Za-z]:\\[^\s""'<>|]+|/[^\s""'<>|]+|~/[^\s""'<>|]+",
            RegexOptions.IgnoreCase);

        foreach (Match match in pathMatches)
        {
            var rawPath = match.Value;
            string fullPath;
            try { fullPath = Path.GetFullPath(rawPath); }
            catch { continue; }

            if (LTAI.Core.PathUtils.SafeResolvePath(ws, rawPath) != null) continue;
            if (LTAI.Core.PathUtils.PathPermissionStore.IsGranted(fullPath)) continue;
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath)) continue;

            Console.WriteLine($"\n⚠️ 检测到工作区外的路径：");
            Console.WriteLine($"  {fullPath}");
            Console.Write("  允许访问？(y=允许一次 / a=总是允许 / n=拒绝): ");
            var key = Console.ReadKey(true);
            Console.WriteLine();
            if (key.Key == ConsoleKey.Y || key.Key == ConsoleKey.A)
            {
                LTAI.Core.PathUtils.PathPermissionStore.Grant(fullPath);
                Console.WriteLine($"  ✅ 已授权");
            }
        }
    }
}
