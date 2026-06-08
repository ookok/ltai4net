using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace LTAI.TUI.Rendering;

/// <summary>
/// Renders a Markdig AST to Spectre.Console markup string.
/// Handles headings, lists, code blocks, blockquotes, tables, and inline formatting.
/// </summary>
public sealed class SpectreMarkdigRenderer
{
    private readonly StringBuilder _sb = new();

    public void Render(MarkdownDocument doc)
    {
        foreach (var block in doc)
            RenderBlock(block);
    }

    public string RenderToString(string markdown)
    {
        _sb.Clear();
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        var doc = Markdig.Markdown.Parse(markdown, pipeline);
        Render(doc);
        return _sb.ToString();
    }

    private void RenderBlock(Block block)
    {
        switch (block)
        {
            case HeadingBlock h: RenderHeading(h); break;
            case ParagraphBlock p: RenderParagraph(p); break;
            case FencedCodeBlock f: RenderFencedCode(f); break;
            case CodeBlock c: RenderCodeBlock(c); break;
            case ListBlock l: RenderList(l); break;
            case QuoteBlock q: RenderQuote(q); break;
            case Markdig.Extensions.Tables.Table t: RenderTable(t); break;
            case ThematicBreakBlock: Newline(); _sb.AppendLine("[grey]────────────────[/]"); break;
        }
    }

    private void RenderHeading(HeadingBlock h)
    {
        Newline();
        var tag = h.Level switch
        {
            1 => "[bold yellow]",
            2 => "[bold]",
            3 => "[bold cyan]",
            _ => "[bold]",
        };
        _sb.Append(tag);
        RenderInlines(h.Inline);
        _sb.AppendLine("[/]");
    }

    private void RenderParagraph(ParagraphBlock p)
    {
        Newline();
        // Check if paragraph contains diff-like content
        var text = GetLiteralTextFromInline(p.Inline);
        if (IsDiffContent(text))
        {
            RenderDiffContent(text);
        }
        else
        {
            RenderInlines(p.Inline);
            _sb.AppendLine();
        }
    }

    private static string GetLiteralTextFromInline(ContainerInline? inlines)
    {
        if (inlines == null) return "";
        var sb = new StringBuilder();
        var inline = inlines.FirstChild;
        while (inline != null)
        {
            if (inline is LiteralInline lit)
                sb.Append(lit.Content.ToString());
            else if (inline is LineBreakInline)
                sb.Append('\n');
            else if (inline is CodeInline code)
                sb.Append(code.Content);
            inline = inline.NextSibling;
        }
        return sb.ToString();
    }

    private static bool IsDiffContent(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var lines = text.Split('\n');
        var diffLines = lines.Count(l =>
        {
            var t = l.TrimStart();
            return t.StartsWith('+') || t.StartsWith('-') || t.StartsWith("@@");
        });
        return diffLines >= 3 && diffLines >= lines.Length * 0.5;
    }

    private void RenderDiffContent(string text)
    {
        var lines = text.Split('\n');
        var boxWidth = Math.Min(SafeWindowWidth - 14, 76);
        _sb.AppendLine("[bold grey]┌─ diff ───────────────────────┐[/]");
        foreach (var line in lines)
        {
            var padded = line.Length <= boxWidth - 4
                ? line + new string(' ', boxWidth - 4 - line.Length)
                : line[..(boxWidth - 7)] + "...";
            if (line.StartsWith('+'))
                _sb.AppendLine($"  [green]│[/] [green]{EscapeMarkup(padded)}[/] [green]│[/]");
            else if (line.StartsWith('-'))
                _sb.AppendLine($"  [red]│[/] [red]{EscapeMarkup(padded)}[/] [red]│[/]");
            else if (line.StartsWith("@@"))
                _sb.AppendLine($"  [cyan]│[/] [cyan]{EscapeMarkup(padded)}[/] [cyan]│[/]");
            else
                _sb.AppendLine($"  [grey]│[/] {EscapeMarkup(padded)} [grey]│[/]");
        }
        _sb.AppendLine($"[bold grey]└{new string('─', boxWidth)}┘[/]");
    }

    private void RenderFencedCode(FencedCodeBlock f)
    {
        Newline();
        var lang = f.Info ?? "";
        var code = string.Join("\n", f.Lines.Lines.Select(l => l.Slice.ToString()));
        var boxWidth = Math.Min(SafeWindowWidth - 14, 76);

        var header = string.IsNullOrEmpty(lang)
            ? $"┌──────┐ ┬╯[dim]📋[/]"
            : $"┌─ {lang} {new string('─', Math.Max(0, boxWidth - lang.Length - 7))}┐ ┬╯[dim]📋[/]";
        _sb.AppendLine($"[bold grey]{header}[/]");

        CodeBlockBuffer.Register(code, lang);

        var lines = code.Split('\n');
        var isDiff = string.Equals(lang, "diff", StringComparison.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var padded = line.Length <= boxWidth - 4
                ? line + new string(' ', boxWidth - 4 - line.Length)
                : line[..(boxWidth - 7)] + "...";
            if (isDiff && line.Length > 0)
            {
                var diffChar = line[0];
                if (diffChar == '+')
                    _sb.AppendLine($"  [green]│[/] [green]{EscapeMarkup(padded)}[/] [green]│[/]");
                else if (diffChar == '-')
                    _sb.AppendLine($"  [red]│[/] [red]{EscapeMarkup(padded)}[/] [red]│[/]");
                else if (line.StartsWith("@@"))
                    _sb.AppendLine($"  [cyan]│[/] [cyan]{EscapeMarkup(padded)}[/] [cyan]│[/]");
                else
                    _sb.AppendLine($"  [grey]│[/] {EscapeMarkup(padded)} [grey]│[/]");
            }
            else
            {
                _sb.AppendLine($"  [grey]│[/] {EscapeMarkup(padded)} [grey]│[/]");
            }
        }
        _sb.AppendLine($"[bold grey]└{new string('─', boxWidth)}┘[/]");
    }

    private void RenderCodeBlock(CodeBlock c)
    {
        Newline();
        var code = string.Join("\n", c.Lines.Lines.Select(l => l.Slice.ToString()));
        _sb.AppendLine($"[grey]{EscapeMarkup(code)}[/]");
    }

    private void RenderList(ListBlock l)
    {
        Newline();
        var isOrdered = l.IsOrdered;
        var index = int.TryParse(l.OrderedStart, out var start) ? start : 1;
        for (int i = 0; i < l.Count; i++)
        {
            if (l[i] is ListItemBlock item)
            {
                // Check for task list in first paragraph inline text
                var isTaskItem = false;
                var taskDone = false;
                if (!isOrdered && item.Count > 0 && item[0] is ParagraphBlock para && para.Inline != null)
                {
                    var firstLit = para.Inline.FirstChild;
                    var text = firstLit is LiteralInline lit ? lit.Content.ToString() : "";
                    if (text.StartsWith("[ ]"))
                    {
                        isTaskItem = true;
                        taskDone = false;
                    }
                    else if (text.StartsWith("[x]") || text.StartsWith("[X]"))
                    {
                        isTaskItem = true;
                        taskDone = true;
                    }
                }

                var prefix = isOrdered ? $"  [grey]{index}.[/] " : "  [green]•[/] ";
                if (isTaskItem)
                {
                    prefix = taskDone
                        ? "  [green]☑️[/] "
                        : "  ⬜ ";
                }
                _sb.Append(prefix);
                RenderChildren(item);
                _sb.AppendLine();
                if (isOrdered) index++;
            }
        }
    }

    private void RenderQuote(QuoteBlock q)
    {
        Newline();
        _sb.Append("  [grey]│[/] [italic]");
        RenderChildren(q);
        _sb.AppendLine("[/]");
    }

    private void RenderTable(Markdig.Extensions.Tables.Table t)
    {
        Newline();
        var alignments = t.ColumnDefinitions?.Select(cd => cd.Alignment ?? TableColumnAlign.Left).ToList();
        var colWidths = new List<int>();
        var rows = new List<List<string>>();

        foreach (var rowObj in t)
        {
            if (rowObj is TableRow row)
            {
                var cells = new List<string>();
                foreach (var cellObj in row)
                {
                    if (cellObj is TableCell cell)
                        cells.Add(RenderTableCellContent(cell));
                }
                rows.Add(cells);
                for (int i = 0; i < cells.Count; i++)
                {
                    var stripped = StripMarkup(cells[i]);
                    if (i >= colWidths.Count) colWidths.Add(stripped.Length);
                    else colWidths[i] = Math.Max(colWidths[i], stripped.Length);
                }
            }
        }

        foreach (var cells in rows)
        {
            var formatted = new List<string>();
            for (int i = 0; i < cells.Count; i++)
            {
                var w = i < colWidths.Count ? colWidths[i] : 0;
                var align = alignments != null && i < alignments.Count ? alignments[i] : TableColumnAlign.Left;
                var cell = cells[i];
                if (align == TableColumnAlign.Right)
                    cell = new string(' ', Math.Max(0, w - StripMarkup(cell).Length)) + cell;
                else if (align == TableColumnAlign.Center)
                {
                    var padding = Math.Max(0, w - StripMarkup(cell).Length);
                    cell = new string(' ', padding / 2) + cell + new string(' ', padding - padding / 2);
                }
                formatted.Add(cell);
            }
            if (formatted.Count > 0)
                _sb.AppendLine("[grey]│[/] " + string.Join(" [grey]│[/] ", formatted) + " [grey]│[/]");
        }
    }

    private static string StripMarkup(string text)
    {
        var result = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '[')
            {
                var end = text.IndexOf(']', i);
                if (end > i) { i = end + 1; continue; }
            }
            result.Append(text[i]);
            i++;
        }
        return result.ToString();
    }

    private string RenderTableCellContent(TableCell cell)
    {
        var buf = new StringBuilder();
        foreach (var block in cell)
        {
            if (block is ParagraphBlock p)
            {
                var inline = p.Inline?.FirstChild;
                while (inline != null)
                {
                    RenderInlineTo(buf, inline);
                    inline = inline.NextSibling;
                }
            }
        }
        return buf.ToString().Trim();
    }

    private void RenderChildren(ContainerBlock container)
    {
        foreach (var child in container)
        {
            if (child is ParagraphBlock p)
                RenderInlines(p.Inline);
            else if (child is ListBlock l)
                RenderList(l);
            else if (child is QuoteBlock q)
                RenderQuote(q);
        }
    }

    private void RenderInlines(ContainerInline? inlines)
    {
        if (inlines == null) return;
        var inline = inlines.FirstChild;
        while (inline != null)
        {
            RenderInlineTo(_sb, inline);
            inline = inline.NextSibling;
        }
    }

    private static void RenderInlineTo(StringBuilder buf, Inline inline)
    {
        switch (inline)
        {
            case LiteralInline lit:
                buf.Append(EscapeMarkup(lit.Content.ToString()));
                break;
            case CodeInline code:
                buf.Append($"[grey]{EscapeMarkup(code.Content)}[/]");
                break;
            case EmphasisInline emp:
                if (emp.DelimiterChar == '~' && emp.DelimiterCount >= 2)
                {
                    // Strikethrough
                    buf.Append("[grey]~~");
                    var sc = emp.FirstChild;
                    while (sc != null) { RenderInlineTo(buf, sc); sc = sc.NextSibling; }
                    buf.Append("~~[/]");
                }
                else
                {
                    var tag = emp.DelimiterCount >= 2 ? "bold" : "italic";
                    buf.Append($"[{tag}]");
                    var child = emp.FirstChild;
                    while (child != null)
                    {
                        RenderInlineTo(buf, child);
                        child = child.NextSibling;
                    }
                    buf.Append("[/]");
                }
                break;
            case LinkInline link:
                var url = link.Url ?? "";
                if (link.IsImage)
                {
                    var alt = GetLiteralText(link);
                    var term = Environment.GetEnvironmentVariable("TERM_PROGRAM") ?? "";
                    var kittySupported = term.Contains("kitty", StringComparison.OrdinalIgnoreCase)
                        || Environment.GetEnvironmentVariable("KITTY_WINDOW_ID") != null;
                    var iterm2Supported = term.Contains("iTerm", StringComparison.OrdinalIgnoreCase);
                    if (kittySupported || iterm2Supported)
                    {
                        var proto = kittySupported ? "Kitty" : "iTerm2";
                        buf.Append($"[dim][{proto} 图片: {EscapeMarkup(alt)} ({EscapeMarkup(url)})[/]");
                    }
                    else
                    {
                        buf.Append($"[grey][🖼 {EscapeMarkup(alt)}][/]");
                    }
                }
                else
                {
                    buf.Append($"[link={EscapeMarkup(url)}]");
                    var lc = link.FirstChild;
                    while (lc != null)
                    {
                        RenderInlineTo(buf, lc);
                        lc = lc.NextSibling;
                    }
                    buf.Append("[/]");
                }
                break;
            case LineBreakInline:
                buf.AppendLine();
                break;
        }
    }

    private static string GetLiteralText(Inline inline)
    {
        if (inline is LiteralInline lit) return lit.Content.ToString();
        if (inline is ContainerInline container)
        {
            var c = container.FirstChild;
            while (c != null)
            {
                if (c is LiteralInline lit2) return lit2.Content.ToString();
                c = c.NextSibling;
            }
        }
        return "";
    }

    private static int SafeWindowWidth
    {
        get { try { return Console.WindowWidth; } catch { return 80; } }
    }

    private void Newline()
    {
        if (_sb.Length > 0 && _sb[^1] != '\n')
            _sb.AppendLine();
    }

    private static string EscapeMarkup(string text) =>
        text.Replace("[", "[[").Replace("]", "]]");

    public override string ToString() => _sb.ToString().TrimEnd();
}
