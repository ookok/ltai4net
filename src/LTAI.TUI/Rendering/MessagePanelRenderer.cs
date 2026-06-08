using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LTAI.Core.I18n;
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
            "user" => (Color.Cyan, BoxBorder.Rounded, $"[bold cyan] 🧑 {Locale.Get("You")} [/]"),
            "assistant" or "ai" => (Color.Green, BoxBorder.Double, $"[bold green] 🤖 {Locale.Get("AI")} [/]"),
            "tool" => (Color.Blue, BoxBorder.Square, $"[bold blue] 🔧 {Locale.Get("Tool")} [/]"),
            "error" => (Color.Red, BoxBorder.Ascii, $"[bold red] ⛔ {Locale.Get("Error")} [/]"),
            "cmd" or "system" => (Color.Yellow, BoxBorder.Square, $"[bold yellow] ⚙️ {Locale.Get("System")} [/]"),
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
        else if (role is "tool")
            combined.Append(RenderToolResult(rawContent));
        else if (role is "cmd" or "system")
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
                        var boxWidth = Math.Min(SafeWindowWidth - 10, 80);
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

    private static string RenderToolResult(string raw)
    {
        raw = raw.Trim();

        // Check for error patterns
        if (raw.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("❌") || raw.StartsWith("[Error]"))
            return $"[red]⛔ {raw.EscapeMarkup()}[/]";

        // Success indicator
        if (raw.StartsWith("Success:", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("✅"))
            return $"[green]✅ {raw.EscapeMarkup()}[/]";

        // Try to parse as JSON for structured rendering
        if (raw.StartsWith('{') || raw.StartsWith('['))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                var sb = new StringBuilder();

                // Detect tabular data (array of objects with same keys)
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array &&
                    doc.RootElement.GetArrayLength() > 0)
                {
                    if (TryRenderAsTable(sb, doc.RootElement))
                        return sb.ToString();
                }

                // Detect single object with rows/columns (SQL result)
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (TryRenderTableFromObject(sb, doc.RootElement))
                        return sb.ToString();
                    if (TryRenderChartSparkline(sb, doc.RootElement))
                        return sb.ToString();
                }

                RenderToolJson(sb, doc.RootElement, 0);
                return sb.ToString();
            }
            catch { /* fall through to plain text */ }
        }

        // Detect table-like text (pipe-delimited or tab-delimited)
        if (TryRenderTextTable(raw))
            return RenderTextTable(raw);

        // Default: plain text with dim styling
        return $"[dim]{raw.EscapeMarkup()}[/]";
    }

    private static bool TryRenderAsTable(StringBuilder sb, System.Text.Json.JsonElement arr)
    {
        var items = arr.EnumerateArray().Take(50).ToList();
        if (items.Count == 0) return false;

        // Check if all items are objects
        if (items.Any(i => i.ValueKind != System.Text.Json.JsonValueKind.Object))
            return false;

        // Get common keys
        var keys = new HashSet<string>();
        foreach (var item in items)
            foreach (var p in item.EnumerateObject())
                keys.Add(p.Name);
        if (keys.Count == 0 || keys.Count > 12) return false; // too many columns

        var orderedKeys = keys.ToList();
        var colWidths = orderedKeys.Select(k => Math.Max(k.Length, items.Max(i =>
        {
            if (i.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                return Math.Min(v.GetString()?.Length ?? 0, 30);
            return 0;
        }))).ToList();
        colWidths = colWidths.Select(w => Math.Clamp(w + 2, 4, 32)).ToList();

        // Draw header
        sb.AppendLine("[bold]┌" + string.Join("┬", colWidths.Select(w => new string('─', w))) + "┐[/]");
        sb.Append("[bold]│[/]");
        for (int k = 0; k < orderedKeys.Count; k++)
        {
            var label = orderedKeys[k].EscapeMarkup();
            var pad = colWidths[k] - label.Length;
            sb.Append($" [bold cyan]{label}{new string(' ', pad)}[/][bold]│[/]");
        }
        sb.AppendLine();
        sb.AppendLine("[bold]├" + string.Join("┼", colWidths.Select(w => new string('─', w))) + "┤[/]");

        foreach (var item in items)
        {
            sb.Append("[bold]│[/]");
            for (int k = 0; k < orderedKeys.Count; k++)
            {
                var val = "";
                if (item.TryGetProperty(orderedKeys[k], out var v))
                {
                    val = v.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.String => v.GetString() ?? "",
                        System.Text.Json.JsonValueKind.Number => v.GetRawText(),
                        System.Text.Json.JsonValueKind.True => "true",
                        System.Text.Json.JsonValueKind.False => "false",
                        _ => v.GetRawText(),
                    };
                    if (val.Length > 30) val = val[..27] + "...";
                }
                var escaped = val.EscapeMarkup();
                var pad = colWidths[k] - escaped.Length;
                sb.Append($" {escaped}{new string(' ', pad)}[bold]│[/]");
            }
            sb.AppendLine();
        }
        sb.AppendLine("[bold]└" + string.Join("┴", colWidths.Select(w => new string('─', w))) + "┘[/]");
        sb.AppendLine($"[grey]共 {arr.GetArrayLength()} 行[/]");
        return true;
    }

    private static bool TryRenderTableFromObject(StringBuilder sb, System.Text.Json.JsonElement obj)
    {
        // Detect { rows: [...], columns: [...] } pattern
        if (!obj.TryGetProperty("rows", out var rows) || rows.ValueKind != System.Text.Json.JsonValueKind.Array)
            return false;
        var rowItems = rows.EnumerateArray().Take(50).ToList();
        if (rowItems.Count == 0) return false;

        // Get columns
        string[]? colNames = null;
        if (obj.TryGetProperty("columns", out var cols) && cols.ValueKind == System.Text.Json.JsonValueKind.Array)
            colNames = cols.EnumerateArray().Select(c => c.GetString() ?? "").ToArray();

        // Try to determine column names from first row
        if (colNames == null && rowItems[0].ValueKind == System.Text.Json.JsonValueKind.Object)
            colNames = rowItems[0].EnumerateObject().Select(p => p.Name).ToArray();

        if (colNames == null || colNames.Length == 0) return false;

        // Same rendering logic as TryRenderAsTable
        var keys = colNames.ToList();
        var colWidths = keys.Select(k => Math.Max(k.Length, rowItems.Max(i =>
        {
            if (i.ValueKind == System.Text.Json.JsonValueKind.Object && i.TryGetProperty(k, out var v))
            {
                if (v.ValueKind == System.Text.Json.JsonValueKind.String)
                    return Math.Min(v.GetString()?.Length ?? 0, 30);
                return v.GetRawText().Length;
            }
            return 0;
        }))).ToList();
        colWidths = colWidths.Select(w => Math.Clamp(w + 2, 4, 32)).ToList();

        sb.AppendLine("[bold]┌" + string.Join("┬", colWidths.Select(w => new string('─', w))) + "┐[/]");
        sb.Append("[bold]│[/]");
        for (int k = 0; k < keys.Count; k++)
        {
            var label = keys[k].EscapeMarkup();
            var pad = colWidths[k] - label.Length;
            sb.Append($" [bold cyan]{label}{new string(' ', pad)}[/][bold]│[/]");
        }
        sb.AppendLine();
        sb.AppendLine("[bold]├" + string.Join("┼", colWidths.Select(w => new string('─', w))) + "┤[/]");

        foreach (var item in rowItems)
        {
            sb.Append("[bold]│[/]");
            for (int k = 0; k < keys.Count; k++)
            {
                var val = "";
                if (item.ValueKind == System.Text.Json.JsonValueKind.Object && item.TryGetProperty(keys[k], out var v))
                {
                    val = v.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.String => v.GetString() ?? "",
                        System.Text.Json.JsonValueKind.Number => v.GetRawText(),
                        System.Text.Json.JsonValueKind.True => "true",
                        System.Text.Json.JsonValueKind.False => "false",
                        _ => v.GetRawText(),
                    };
                    if (val.Length > 30) val = val[..27] + "...";
                }
                var escaped = val.EscapeMarkup();
                var pad = colWidths[k] - escaped.Length;
                sb.Append($" {escaped}{new string(' ', pad)}[bold]│[/]");
            }
            sb.AppendLine();
        }
        sb.AppendLine("[bold]└" + string.Join("┴", colWidths.Select(w => new string('─', w))) + "┘[/]");
        sb.AppendLine($"[grey]共 {rows.GetArrayLength()} 行[/]");
        return true;
    }

    private static bool TryRenderChartSparkline(StringBuilder sb, System.Text.Json.JsonElement obj)
    {
        // Detect { data: [...], type: "chart" } pattern
        if (!obj.TryGetProperty("type", out var typeProp) || typeProp.GetString() is not ("chart" or "bar" or "line"))
        {
            // Also detect { values: [...] } or { series: [...] }
            if (!obj.TryGetProperty("values", out _) && !obj.TryGetProperty("series", out _) && !obj.TryGetProperty("data", out _))
                return false;
        }

        // Extract numeric values
        var nums = new List<double>();
        foreach (var propName in new[] { "values", "data", "series", "scores", "counts" })
        {
            if (!obj.TryGetProperty(propName, out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
                continue;
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind == System.Text.Json.JsonValueKind.Number)
                    nums.Add(el.GetDouble());
                else if (el.ValueKind == System.Text.Json.JsonValueKind.Object &&
                         el.TryGetProperty("value", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number)
                    nums.Add(v.GetDouble());
            }
            if (nums.Count > 0) break;
        }

        if (nums.Count < 2) return false;

        // Render sparkline
        var min = nums.Min();
        var max = nums.Max();
        var range = Math.Max(max - min, 0.001);
        var bars = "▁▂▃▄▅▆▇█";
        var lineLen = Math.Min(nums.Count, 60);
        sb.AppendLine("[bold]📊 数据可视化[/]");
        sb.Append("  [green]");
        for (int i = 0; i < lineLen; i++)
        {
            var idx = (int)((nums[i] - min) / range * 7);
            sb.Append(bars[Math.Clamp(idx, 0, 7)]);
        }
        sb.AppendLine("[/]");
        if (nums.Count > lineLen)
            sb.AppendLine($"  [grey]... 还有 {nums.Count - lineLen} 个值[/]");
        sb.AppendLine($"  [dim]  min={min:F2}  max={max:F2}  avg={nums.Average():F2}[/]");

        // Add chart title if available
        if (obj.TryGetProperty("title", out var title))
            sb.AppendLine($"  [dim]{title.GetString()?.EscapeMarkup()}[/]");

        return true;
    }

    private static bool TryRenderTextTable(string raw)
    {
        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 3) return false;
        var pipeCounts = lines.Take(3).Select(l => l.Count(c => c == '|')).ToList();
        return pipeCounts[0] >= 2 && pipeCounts[1] >= 2 && pipeCounts[2] >= 2;
    }

    private static string RenderTextTable(string raw)
    {
        var sb = new StringBuilder();
        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var headerLine = lines.FirstOrDefault(l => !l.Contains("---") && l.Contains('|'));
        if (headerLine == null) return $"[dim]{raw.EscapeMarkup()}[/]";

        // Simple pipe rendering
        sb.AppendLine("[bold]┌──────────────┐[/]");
        foreach (var line in lines)
        {
            if (line.Contains("---")) continue;
            var cells = line.Split('|', StringSplitOptions.RemoveEmptyEntries);
            sb.Append("[bold]│[/] ");
            foreach (var cell in cells)
                sb.Append($"{cell.Trim().EscapeMarkup()} ");
            sb.AppendLine("[bold]│[/]");
        }
        sb.AppendLine("[bold]└──────────────┘[/]");
        return sb.ToString();
    }

    private static void RenderToolJson(StringBuilder sb, System.Text.Json.JsonElement el, int depth)
    {
        var indent = new string(' ', depth * 2);
        switch (el.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                sb.AppendLine($"{indent}[grey]{{[/]");
                var first = true;
                foreach (var prop in el.EnumerateObject())
                {
                    if (!first) sb.AppendLine(",");
                    first = false;
                    sb.Append($"{indent}  [cyan]{prop.Name.EscapeMarkup()}[/]: ");
                    RenderToolJsonValue(sb, prop.Value, depth + 1);
                }
                sb.AppendLine();
                sb.Append($"{indent}[grey]}}[/]");
                break;
            case System.Text.Json.JsonValueKind.Array:
                sb.AppendLine($"{indent}[grey][[/]");
                var idx = 0;
                foreach (var item in el.EnumerateArray())
                {
                    if (idx > 0) sb.AppendLine(",");
                    sb.Append($"{indent}  ");
                    RenderToolJsonValue(sb, item, depth + 1);
                    idx++;
                }
                sb.AppendLine();
                sb.Append($"{indent}[grey]][/]");
                break;
            default:
                RenderToolJsonValue(sb, el, depth);
                break;
        }
    }

    private static void RenderToolJsonValue(StringBuilder sb, System.Text.Json.JsonElement el, int depth)
    {
        switch (el.ValueKind)
        {
            case System.Text.Json.JsonValueKind.String:
                var str = el.GetString() ?? "";
                if (str.Length > 200) str = str[..197] + "...";
                sb.Append($"[green]\"{str.EscapeMarkup()}\"[/]");
                break;
            case System.Text.Json.JsonValueKind.Number:
                sb.Append($"[yellow]{el.GetRawText()}[/]");
                break;
            case System.Text.Json.JsonValueKind.True:
            case System.Text.Json.JsonValueKind.False:
                sb.Append($"[magenta]{el.GetRawText()}[/]");
                break;
            case System.Text.Json.JsonValueKind.Null:
                sb.Append("[grey]null[/]");
                break;
            case System.Text.Json.JsonValueKind.Object:
            case System.Text.Json.JsonValueKind.Array:
                RenderToolJson(sb, el, depth);
                break;
            default:
                sb.Append(el.GetRawText().EscapeMarkup());
                break;
        }
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

    private static int SafeWindowWidth
    {
        get { try { return Console.WindowWidth; } catch { return 80; } }
    }

    private static int SafeWindowHeight
    {
        get { try { return Console.WindowHeight; } catch { return 24; } }
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
