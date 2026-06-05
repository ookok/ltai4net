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
        RenderInlines(p.Inline);
        _sb.AppendLine();
    }

    private void RenderFencedCode(FencedCodeBlock f)
    {
        Newline();
        var lang = f.Info ?? "";
        var code = string.Join("\n", f.Lines.Lines.Select(l => l.Slice.ToString()));
        var boxWidth = Math.Min(Console.WindowWidth - 10, 80);

        var header = string.IsNullOrEmpty(lang) ? "┌──────┐" : $"┌─ {lang} {new string('─', Math.Max(0, boxWidth - lang.Length - 3))}┐";
        _sb.AppendLine($"[bold grey]{header}[/]");

        var lines = code.Split('\n');
        foreach (var line in lines)
        {
            var padded = line.Length <= boxWidth - 4
                ? line + new string(' ', boxWidth - 4 - line.Length)
                : line[..(boxWidth - 7)] + "...";
            _sb.AppendLine($"  [grey]│[/] {EscapeMarkup(padded)} [grey]│[/]");
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
                var prefix = isOrdered ? $"  [grey]{index}.[/] " : "  [green]•[/] ";
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
                if (cells.Count > 0)
                    _sb.AppendLine("[grey]│[/] " + string.Join(" [grey]│[/] ", cells) + " [grey]│[/]");
            }
        }
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
                var tag = emp.DelimiterCount >= 2 ? "bold" : "italic";
                buf.Append($"[{tag}]");
                var child = emp.FirstChild;
                while (child != null)
                {
                    RenderInlineTo(buf, child);
                    child = child.NextSibling;
                }
                buf.Append("[/]");
                break;
            case LinkInline link:
                var url = link.Url ?? "";
                if (link.IsImage)
                {
                    var alt = GetLiteralText(link);
                    buf.Append($"[grey][[图片: {EscapeMarkup(alt)} ({EscapeMarkup(url)})]][/]");
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

    private void Newline()
    {
        if (_sb.Length > 0 && _sb[^1] != '\n')
            _sb.AppendLine();
    }

    private static string EscapeMarkup(string text) =>
        text.Replace("[", "[[").Replace("]", "]]");

    public override string ToString() => _sb.ToString().TrimEnd();
}
