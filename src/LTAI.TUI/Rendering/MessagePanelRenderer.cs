using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LTAI.Core.Rendering;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LTAI.TUI.Rendering;

public sealed class MessagePanelRenderer
{
    private readonly IAnsiConsole _console;

    public MessagePanelRenderer(IAnsiConsole console)
    {
        _console = console ?? AnsiConsole.Console;
    }

    // LRU cache for code block panels
    private const int MaxPanelCache = 128;
    private static readonly ConcurrentDictionary<string, Panel> _panelCache = new();
    private static readonly ConcurrentQueue<string> _panelCacheOrder = new();

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

        bool isAssistant = role.ToLowerInvariant() is "assistant" or "ai";
        bool hasReasoning = isAssistant && !string.IsNullOrEmpty(reasoning);
        bool isExpanded = hasReasoning && expandedMessages?.Contains(historyIndex) == true;

        var combined = new StringBuilder();
        if (hasReasoning)
        {
            combined.AppendLine(isExpanded
                ? "[dim][[−]] 推理过程[/]"
                : $"[dim][[+]] 推理过程 ([green]{reasoning!.Split('\n').Length}[/] 行)[/]");
        }
        if (isExpanded && hasReasoning)
        {
            combined.AppendLine(reasoning);
            combined.AppendLine("[grey]───[/]");
        }

        if (isAssistant)
            combined.Append(ChatRenderer.MdToPanelContent(rawContent));
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

    public Panel BuildCodeBlockPanel(string code, string? lang)
    {
        var cacheKey = PanelCacheKey(code, lang);
        if (_panelCache.TryGetValue(cacheKey, out var cached)) return cached;

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

        var panel = new Panel(Align.Left(new Markup(content.ToString().TrimEnd()), VerticalAlignment.Top))
        {
            Border = BoxBorder.Heavy,
            BorderStyle = new Style(Color.Grey42),
            Header = new PanelHeader($"[bold grey] {(lang ?? "code").EscapeMarkup()} [/]", Justify.Left),
            Padding = new Padding(2, 0, 2, 0),
            Expand = true,
        };
        PanelCacheAdd(cacheKey, panel);
        return panel;
    }

    public Panel BuildMessagesPanel(
        List<(string role, IRenderable? rendered, string rawContent, string? reasoning)> history,
        string? streamingContent,
        List<(string name, string args, string result)>? toolCalls,
        int scrollOffset,
        int maxVisibleMessages,
        HashSet<int>? expandedMessages)
    {
        var allMessages = new List<IRenderable>();

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

        if (!string.IsNullOrEmpty(streamingContent))
        {
            var combined = new StringBuilder();
            if (toolCalls is { Count: > 0 })
                combined.AppendLine(ChatRenderer.RenderToolCallsAsTreeStatic(toolCalls));
            var raw = streamingContent;
            if (MarkdownUtils.HasUnclosedFence(raw))
            {
                var lastFence = raw.LastIndexOf("```", StringComparison.Ordinal);
                if (lastFence > 0)
                {
                    var completePart = raw[..lastFence];
                    var fenceLineEnd = raw.IndexOf('\n', lastFence);
                    var codeLang = fenceLineEnd > lastFence ? raw[(lastFence + 3)..fenceLineEnd].Trim() : "";
                    var incompleteCode = fenceLineEnd > 0 ? raw[(fenceLineEnd + 1)..] : "";
                    combined.Append(ChatRenderer.MdToPanelContent(completePart));
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
                    combined.Append(ChatRenderer.MdToPanelContent(raw));
                }
            }
            else
            {
                combined.Append(ChatRenderer.MdToPanelContent(raw));
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

        if (allMessages.Count == 0)
            return BuildWelcomePanel();

        var messages = new List<IRenderable>();
        int totalMessages = allMessages.Count;
        int visibleCount = Math.Min(maxVisibleMessages, totalMessages);
        int startIdx = Math.Max(0, totalMessages - visibleCount - scrollOffset);
        int endIdx = Math.Min(totalMessages, startIdx + maxVisibleMessages);
        if (startIdx < 0) startIdx = 0;

        if (startIdx > 0)
            messages.Add(new Markup($"[dim]↕ 以上 {startIdx} 条消息已滚动  Shift+↑↓/PgUp/PgDn 翻页  (共 {totalMessages} 条)[/]"));
        messages.AddRange(allMessages.GetRange(startIdx, endIdx - startIdx));
        if (scrollOffset > 0 && endIdx < totalMessages)
            messages.Add(new Markup($"[dim]↓ 还有 {totalMessages - endIdx} 条消息在后面  按 Shift+↓ 或 PgDn 滚动[/]"));

        return new Panel(new Rows(messages)).Border(BoxBorder.None).Expand();
    }

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
}
