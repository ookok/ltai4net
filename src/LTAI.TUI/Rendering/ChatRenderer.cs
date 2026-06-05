using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LTAI.Core.Configuration;
using LTAI.Core.Rendering;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LTAI.TUI.Rendering;

/// <summary>
/// Pure rendering layer for the LTAI chat TUI.
/// All methods are deterministic — no I/O, no side effects.
/// Testable with <c>new ChatRenderer(new TestConsole())</c>.
/// </summary>
public sealed class ChatRenderer
{
    private readonly IAnsiConsole _console;

    public ChatRenderer(IAnsiConsole console)
    {
        _console = console ?? AnsiConsole.Console;
    }

    // ═══════════════════════════════════════════════
    //  Init
    // ═══════════════════════════════════════════════

    /// <summary>Show a Status spinner during the init phase (before Live mode).</summary>
    public async Task ShowInitStatusAsync(Func<StatusContext, Task> action, string initialMessage)
    {
        try
        {
            await _console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync(initialMessage, action)
                .ConfigureAwait(false);
        }
        catch { /* non-interactive terminal */ }
    }

    // ═══════════════════════════════════════════════
    //  Message Panels
    // ═══════════════════════════════════════════════

    /// <summary>Build a colored-bordered Panel for a chat message, with optional collapsible reasoning.</summary>
    public Panel BuildMessagePanel(string role, string rawContent, int historyIndex = -1,
        string? reasoning = null, HashSet<int>? expandedMessages = null)
    {
        var (color, border, header) = (role.ToLowerInvariant()) switch
        {
            "user" => (Color.Cyan, BoxBorder.Rounded, "[bold cyan] 🧑 你 [/]"),
            "assistant" or "ai" => (Color.Green, BoxBorder.Double, "[bold green] 🤖 AI [/]"),
            "tool" => (Color.Blue, BoxBorder.Square, "[bold blue] 🔧 工具 [/]"),
            "error" => (Color.Red, BoxBorder.Ascii, "[bold red] ⛔ 错误 [/]"),
            "cmd" or "system" => (Color.Yellow, BoxBorder.Square, "[bold yellow] ⚙️ 系统 [/]"),
            _ => (Color.Grey, BoxBorder.None, "[bold grey] ℹ️ [/]"),
        };

        // 可折叠推理过程
        bool isAssistant = role.ToLowerInvariant() is "assistant" or "ai";
        bool hasReasoning = isAssistant && !string.IsNullOrEmpty(reasoning);
        bool isExpanded = hasReasoning && expandedMessages?.Contains(historyIndex) == true;

        var combined = new StringBuilder();

        // 展开标记
        if (hasReasoning)
        {
            combined.AppendLine(isExpanded
                ? "[dim][[−]] 推理过程[/]"
                : $"[dim][[+]] 推理过程 ([green]{reasoning!.Split('\n').Length}[/] 行)[/]");
        }

        // 推理过程（仅展开时显示）
        if (isExpanded && hasReasoning)
        {
            combined.AppendLine(reasoning);
            combined.AppendLine("[grey]───[/]");
        }

        // AI 回答
        if (isAssistant)
            combined.Append(MdToPanelContent(rawContent));
        else if (role is "cmd" or "system" or "tool")
            combined.Append(rawContent);
        else
            combined.Append(rawContent.EscapeMarkup());

        var content = (IRenderable)new Markup(combined.ToString().TrimEnd());

        return new Panel(Align.Left(content, VerticalAlignment.Top))
        {
            Border = border,
            Header = new PanelHeader(header, Justify.Left),
            BorderStyle = new Style(color),
            Padding = new Padding(1, 0, 1, 0),
            Expand = true,
        };
    }

    // LRU cache for rendered panels
    private const int MaxPanelCache = 128;
    private static readonly ConcurrentDictionary<string, Panel> _panelCache = new();
    private static readonly ConcurrentQueue<string> _panelCacheOrder = new();

    private static string PanelCacheKey(string code, string? lang)
    {
        var key = $"{lang ?? ""}|{code}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes);
    }

    private static void PanelCacheAdd(string key, Panel panel)
    {
        _panelCache[key] = panel;
        _panelCacheOrder.Enqueue(key);
        while (_panelCacheOrder.Count > MaxPanelCache && _panelCacheOrder.TryDequeue(out var old))
            _panelCache.TryRemove(old, out _);
    }

    private static string HighlightLine(string line, HashSet<string>? keywords)
    {
        if (line.Length == 0) return "";
        // simple syntax highlighting for TUI
        var kw = keywords;
        var result = new StringBuilder();
        int i = 0;
        while (i < line.Length)
        {
            if (line[i] == '"')
            {
                var end = line.IndexOf('"', i + 1);
                if (end < 0) end = line.Length - 1;
                result.Append($"[green]{line[i..(end + 1)].EscapeMarkup()}[/]");
                i = end + 1;
                continue;
            }
            if (i < line.Length - 1 && ((line[i] == '/' && line[i + 1] == '/') || line[i] == '#'))
            {
                result.Append($"[grey]{line[i..].EscapeMarkup()}[/]");
                break;
            }
            if (char.IsLetter(line[i]) || line[i] == '_')
            {
                var end = i;
                while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_')) end++;
                var word = line[i..end];
                if (kw != null && kw.Contains(word))
                    result.Append($"[yellow]{word.EscapeMarkup()}[/]");
                else
                    result.Append(word.EscapeMarkup());
                i = end;
                continue;
            }
            if (char.IsDigit(line[i]) || (line[i] == '-' && i + 1 < line.Length && char.IsDigit(line[i + 1])))
            {
                var end = i;
                while (end < line.Length && (char.IsDigit(line[end]) || line[end] == '.' || line[end] == 'f' || line[end] == 'L' || line[end] == 'd' || line[end] == 'x')) end++;
                result.Append($"[cyan]{line[i..end].EscapeMarkup()}[/]");
                i = end;
                continue;
            }
            result.Append(line[i].ToString().EscapeMarkup());
            i++;
        }
        return result.ToString();
    }

    /// <summary>Build a code block panel with language label, heavy border, and syntax highlighting.</summary>
    public Panel BuildCodeBlockPanel(string code, string? lang)
    {
        var cacheKey = PanelCacheKey(code, lang);
        if (_panelCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var keywords = MarkdownUtils.GetKeywords(lang);
        var lines = code.Split('\n');
        var maxLines = 60;
        var content = new StringBuilder();
        var linePad = lines.Length.ToString().Length;

        for (int i = 0; i < lines.Length && i < maxLines; i++)
        {
            var lineNum = (i + 1).ToString().PadLeft(linePad);
            content.AppendLine($"[grey]{lineNum.EscapeMarkup()}[/]  {HighlightLine(lines[i], keywords)}");
        }

        if (lines.Length > maxLines)
            content.AppendLine($"[grey italic]... 已截断 {lines.Length - maxLines} 行[/]");

        var panel = new Panel(
            Align.Left(new Markup(content.ToString().TrimEnd()), VerticalAlignment.Top))
        {
            Border = BoxBorder.Heavy,
            BorderStyle = new Style(Color.Grey42),
            Header = new PanelHeader(
                $"[bold grey] {(lang ?? "code").EscapeMarkup()} [/]", Justify.Left),
            Padding = new Padding(2, 0, 2, 0),
            Expand = true,
        };
        PanelCacheAdd(cacheKey, panel);
        return panel;
    }

    // ═══════════════════════════════════════════════
    //  Messages area
    // ═══════════════════════════════════════════════

    /// <summary>Build the Messages panel from history + optional streaming content.</summary>
    public Panel BuildMessagesPanel(
        List<(string role, IRenderable? rendered, string rawContent, string? reasoning)> history,
        string streamingContent,
        List<(string name, string args, string result)> toolCalls,
        int scrollOffset,
        int maxVisibleMessages,
        HashSet<int> expandedMessages)
    {
        var allMessages = new List<IRenderable>();

        // Rendered history
        for (int i = 0; i < history.Count; i++)
        {
            var (role, rendered, rawContent, reasoning) = history[i];
            Panel panel;
            if (rendered != null && rendered is Panel p)
                panel = p;
            else
                panel = BuildMessagePanel(role, rawContent, i, reasoning, expandedMessages);
            allMessages.Add(panel);
        }

        // Streaming panel
        if (!string.IsNullOrEmpty(streamingContent))
        {
            var combined = new StringBuilder();
            if (toolCalls.Count > 0)
                combined.AppendLine(RenderToolCallsAsTree(toolCalls));
            // Code fence awareness: 拆分为完整部分 + 流式尾部
            var raw = streamingContent;
            if (MarkdownUtils.HasUnclosedFence(raw))
            {
                // 找到最后一个未闭合的 ```，之前的部分是完整的
                var lastFence = raw.LastIndexOf("```", StringComparison.Ordinal);
                if (lastFence > 0)
                {
                    var completePart = raw[..lastFence]; // fence start line + before
                    var fenceLineEnd = raw.IndexOf('\n', lastFence);
                    var codeLang = fenceLineEnd > lastFence
                        ? raw[(lastFence + 3)..fenceLineEnd].Trim()
                        : "";
                    var incompleteCode = fenceLineEnd > 0 ? raw[(fenceLineEnd + 1)..] : "";

                    // 完整部分用 Markdig 渲染
                    combined.Append(MdToPanelContent(completePart));
                    // 流式代码尾部用简单灰色显示
                    if (!string.IsNullOrEmpty(incompleteCode))
                    {
                        combined.AppendLine($"\n[bold grey]┌─ {codeLang.EscapeMarkup()} ─(生成中)─┐[/]");
                        var boxWidth = Math.Min(Console.WindowWidth - 10, 80);
                        foreach (var cl in incompleteCode.Split('\n'))
                            combined.AppendLine($"  [grey]│[/] {cl.EscapeMarkup()} [grey]│[/]");
                        combined.AppendLine($"[bold grey]└{new string('─', boxWidth)}┘[/]");
                    }
                }
                else
                {
                    combined.Append(MdToPanelContent(raw));
                }
            }
            else
            {
                combined.Append(MdToPanelContent(raw));
            }
            var rendered = combined.ToString().TrimEnd();
            var content = rendered.Length > 0
                ? (IRenderable)new Markup(rendered)
                : new Markup("[grey]等待 AI 回复...[/]");

            allMessages.Add(new Panel(Align.Left(content, VerticalAlignment.Top))
            {
                Border = BoxBorder.Double,
                Header = new PanelHeader("[bold green] 🤖 AI 回复中... [/]", Justify.Left),
                BorderStyle = new Style(Color.Green),
                Padding = new Padding(1, 0, 1, 0),
                Expand = true,
            });
        }

        // Empty → welcome
        if (allMessages.Count == 0)
            return BuildWelcomePanel();

        // Viewport clip
        var messages = new List<IRenderable>();
        int totalMessages = allMessages.Count;
        int visibleCount = Math.Min(maxVisibleMessages, totalMessages);
        int startIdx = Math.Max(0, totalMessages - visibleCount - scrollOffset);
        int endIdx = Math.Min(totalMessages, startIdx + maxVisibleMessages);
        if (startIdx < 0) startIdx = 0;

        if (startIdx > 0)
        {
            messages.Add(new Markup(
                $"[dim]↕ 以上 {startIdx} 条消息已滚动  Shift+↑↓/PgUp/PgDn 翻页  (共 {totalMessages} 条)[/]"));
        }
        messages.AddRange(allMessages.GetRange(startIdx, endIdx - startIdx));

        if (scrollOffset > 0 && endIdx < totalMessages)
        {
            messages.Add(new Markup(
                $"[dim]↓ 还有 {totalMessages - endIdx} 条消息在后面  按 Shift+↓ 或 PgDn 滚动[/]"));
        }

        return new Panel(new Rows(messages))
            .Border(BoxBorder.None)
            .Expand();
    }

    /// <summary>Welcome panel shown on first launch.</summary>
    public Panel BuildWelcomePanel()
    {
        return new Panel(
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
            .Expand();
    }

    // ═══════════════════════════════════════════════
    //  Footer
    // ═══════════════════════════════════════════════

    /// <summary>Build the footer panel with stats + multi-line input + optional suggestions.</summary>
    public Panel BuildFooter(
        string pickerText,
        string statusText,
        bool isFirstEmpty,
        List<string> inputLines,
        int cursorLine,
        int cursorCol,
        int maxInputLines,
        List<SlashCommands.SuggestionItem>? suggestions = null,
        int selIdx = -1)
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

            renders.Add(new Markup(
                $"[bold]{m}[/]  [grey]·[/]  Token: {UsageTracker.TotalTokens:N0}" +
                $"  [grey]·[/]  费用: {UsageTracker.CostDisplay.EscapeMarkup()}" +
                (string.IsNullOrEmpty(tps) ? "" : $"  [grey]·[/]  {tps}") +
                $"  [grey]·[/]  请求: {r}"));

            renders.Add(new Markup(
                $"余额: {b}  [grey]·[/]  缓存: {UsageTracker.CacheHitRate:F1}%" +
                (tc > 0 ? $"  [grey]·[/]  工具: {tc}次" : "") +
                (saved != "¥0.0000" ? $"  [grey]·[/]  节省: {saved}" : "")));

            // 第3行：上下文窗口用量（自动 truncate 提示）
            var ctxText = UsageTracker.ContextText();
            if (!string.IsNullOrEmpty(ctxText))
            {
                var ratio = UsageTracker.ContextRatio();
                var ctxColor = ratio < 0.5 ? "grey" : ratio < 0.75 ? "yellow" : "red";
                renders.Add(new Markup(
                    $"[{ctxColor}]上下文:[/] {ctxText.EscapeMarkup()}" +
                    (ratio > 0.75 ? "  [red]⚠ 即将压缩[/]" : "")));
            }
        }
        else
        {
            renders.Add(new Markup("[grey]等待首次请求...  输入消息开始对话[/]"));
        }

        // Status line
        if (!string.IsNullOrEmpty(statusText))
            renders.Add(new Markup(statusText));

        // Input area
        if (!string.IsNullOrEmpty(pickerText))
        {
            // Picker mode: input + inline suggestions
            var cursorBlink = Environment.TickCount % 1000 < 530;
            var cursor = cursorBlink ? "[bold deepskyblue1]▌[/]" : " ";
            renders.Add(new Markup($"{cursor} {pickerText.EscapeMarkup()}"));

            if (suggestions != null && suggestions.Count > 0)
            {
                var displayed = suggestions.Take(6).ToList();
                var suggestionText = new StringBuilder();
                for (int i = 0; i < displayed.Count; i++)
                {
                    var s = displayed[i];
                    var isSelected = i == selIdx;
                    var cmd = s.Completion;
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
            var showWatermark = (inputLines.Count == 1 && inputLines[0].Length == 0) && isFirstEmpty;
            var cursorBlink = Environment.TickCount % 1000 < 530;

            if (showWatermark)
            {
                var cursor = cursorBlink ? "[bold deepskyblue1]▌[/]" : " ";
                renders.Add(new Markup(
                    $"{cursor} [dim]│[/][grey] 输入消息  SEnter=发送  Enter=换行  ↑↓=光标  /开命令  Ctrl+↑↓=历史  /[/]"));
            }
            else
            {
                var visibleStart = Math.Max(0, cursorLine - maxInputLines + 1);
                var visibleLines = inputLines
                    .Skip(visibleStart)
                    .Take(maxInputLines)
                    .ToList();

                foreach (var (line, idx) in visibleLines.Select((l, i) => (l, i)))
                {
                    var lineNum = visibleStart + idx;
                    var isCursorLine = lineNum == cursorLine;
                    var prefix = isCursorLine && cursorBlink ? "[bold deepskyblue1]▌[/]" :
                                 isCursorLine ? " [grey]▌[/]" : "  ";

                    var colored = HighlightCommands(line.EscapeMarkup());
                    renders.Add(new Markup($"{prefix} {colored}"));
                }
            }
        }

        return new Panel(new Rows(renders.ToArray()))
            .Border(BoxBorder.None)
            .Expand();
    }

    // ═══════════════════════════════════════════════
    //  Markdown → Spectre markup
    // ═══════════════════════════════════════════════

    public string MdToPanelContent(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        try
        {
            var doc = Markdig.Markdown.Parse(text);
            var spectreRenderer = new SpectreMarkdigRenderer();
            spectreRenderer.Render(doc);
            var result = spectreRenderer.ToString().TrimEnd();
            return result.Length > 0 ? result : "";
        }
        catch
        {
            // Fallback: basic escaping
            return text.EscapeMarkup();
        }
    }

    // ── Temporary references kept for ChatLayout backward compat ──
    private string MdLineToSpectre(string line) => MdToPanelContent(line);
    private string InlineMdToSpectre(string text) => text;

    // ═══════════════════════════════════════════════
    //  Tool call tree
    // ═══════════════════════════════════════════════

    public string RenderToolCallsAsTree(List<(string name, string args, string result)> calls)
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

    // ═══════════════════════════════════════════════
    //  Command highlighting
    // ═══════════════════════════════════════════════

    public string HighlightCommands(string escaped)
    {
        escaped = Regex.Replace(escaped, @"(^|\s)(/[a-zA-Z][\w-]*)",
            m => m.Groups[1].Value + "[bold yellow]" + m.Groups[2].Value + "[/]");
        escaped = Regex.Replace(escaped, @"(^|\s)(#[\w-]+)",
            m => m.Groups[1].Value + "[bold cyan]" + m.Groups[2].Value + "[/]");
        return escaped;
    }

    // ═══════════════════════════════════════════════
    //  Utilities
    // ═══════════════════════════════════════════════

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";

    public static readonly string[] PulseFrames =
    [
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
}
