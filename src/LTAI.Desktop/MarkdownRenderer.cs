using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace LTAI.Desktop;

/// <summary>
/// Markdown renderer for Avalonia with syntax-highlighted code blocks.
/// Highlighting uses regex patterns (keywords, strings, comments, numbers) — no external dependencies.
/// </summary>
public static class MarkdownRenderer
{
    // Syntax highlighting: keyword patterns per language
    private static readonly Dictionary<string, string[]> KeywordSets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["csharp"] = new[] { "class", "struct", "interface", "enum", "record", "namespace", "using",
            "public", "private", "protected", "internal", "static", "readonly", "virtual", "abstract",
            "override", "async", "await", "new", "return", "if", "else", "for", "foreach", "while",
            "do", "switch", "case", "break", "continue", "try", "catch", "finally", "throw",
            "var", "void", "int", "string", "bool", "double", "float", "long", "char", "object",
            "true", "false", "null", "this", "base", "in", "out", "ref", "is", "as", "typeof",
            "get", "set", "value", "where", "select", "from" },
        ["python"] = new[] { "class", "def", "return", "if", "elif", "else", "for", "while",
            "try", "except", "finally", "import", "from", "as", "with", "yield", "lambda",
            "True", "False", "None", "self", "and", "or", "not", "in", "is", "async", "await",
            "raise", "pass", "break", "continue", "global", "nonlocal" },
        ["javascript"] = new[] { "function", "class", "const", "let", "var", "return", "if", "else",
            "for", "while", "do", "switch", "case", "break", "continue", "try", "catch", "finally",
            "throw", "new", "this", "async", "await", "import", "export", "default", "from",
            "true", "false", "null", "undefined" },
    };

    private static readonly Regex KeywordRx = new(@"\b([a-zA-Z_]\w*)\b");
    private static readonly Regex StringRx = new(@"""([^""\\]*(\\.[^""\\]*)*)""|'[^']*'");
    private static readonly Regex CommentRx = new(@"(//[^\n]*|#[^\n]*)");
    private static readonly Regex NumberRx = new(@"\b(\d+\.?\d*[fFlLdD]?)\b");
    private static readonly Regex OrderedListRx = new(@"^(\d+)\.\s");
    private static readonly Regex TableSepRx = new(@"^\|[\s\-:]+\|$");
    private static readonly Regex InlineFormatRx = new(
        @"\*\*(.+?)\*\*|__(.+?)__|\*(.+?)\*|_(.+?)_|`(.+?)`|\[(.+?)\]\(([^)]+)\)|~~(.+?)~~");

    public static InlineCollection Render(string text, InlineCollection inlines)
    {
        var lines = text.Split('\n');
        var inCode = false;
        var codeLang = "";
        var codeBuf = new System.Text.StringBuilder();

        for (int li = 0; li < lines.Length; li++)
        {
            var line = lines[li];
            if (li > 0 && !inCode) inlines.Add(new LineBreak());

            if (line.TrimStart().StartsWith("```"))
            {
                if (!inCode) { inCode = true; codeLang = line.TrimStart()[3..].Trim(); codeBuf.Clear(); continue; }
                else { inCode = false; AddCodeBlock(codeBuf.ToString().TrimEnd(), codeLang, inlines); continue; }
            }
            if (inCode) { codeBuf.AppendLine(line); continue; }

            if (line.StartsWith("### ")) { inlines.Add(Run(line[4..], weight: FontWeight.Bold, size: 15)); continue; }
            if (line.StartsWith("## ")) { inlines.Add(Run(line[3..], weight: FontWeight.Bold, size: 17)); continue; }
            if (line.StartsWith("# ")) { inlines.Add(Run(line[2..], weight: FontWeight.Bold, size: 19)); continue; }
            if (line.StartsWith("> ")) { inlines.Add(Run(line, color: LtaiTheme.TextDim, italic: true)); continue; }
            if (line.StartsWith("- ") || line.StartsWith("* "))
            { inlines.Add(Run("  \u2022 ", color: LtaiTheme.AccentDNA)); RenderSpan(line[2..], inlines); continue; }
            var ol = OrderedListRx.Match(line);
            if (ol.Success) { inlines.Add(Run($"  {ol.Groups[1].Value}. ", color: LtaiTheme.AccentDNA)); RenderSpan(line[ol.Length..], inlines); continue; }
            var trimmedLine = line.TrimStart();
            if (trimmedLine.StartsWith('|') && line.TrimEnd().EndsWith('|'))
            {
                if (TableSepRx.IsMatch(trimmedLine)) continue;
                var cells = trimmedLine.Split('|', StringSplitOptions.RemoveEmptyEntries)
                    .Select(c => c.Trim()).ToList();
                for (int ci = 0; ci < cells.Count; ci++)
                {
                    if (ci > 0) inlines.Add(Run(" \u2502 ", color: LtaiTheme.TextDim, font: "Consolas"));
                    RenderSpan(cells[ci], inlines);
                }
                continue;
            }
            RenderSpan(line, inlines);
        }
        return inlines;
    }

    private static void AddCodeBlock(string code, string lang, InlineCollection inlines)
    {
        var border = new Border
        {
            Background = LtaiTheme.Sbb(LtaiTheme.CodeBg),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.CodeBorder),
            BorderThickness = new(1),
            CornerRadius = new(4),
            Padding = new(8),
            Margin = new(0, 2),
        };
        var stack = new StackPanel();

        // Determine keyword set
        var keywords = KeywordSets.Values.FirstOrDefault();
        foreach (var (k, v) in KeywordSets)
            if (lang.Contains(k, StringComparison.OrdinalIgnoreCase)) { keywords = v; break; }

        foreach (var line in code.Split('\n'))
        {
            var tb = new TextBlock
            {
                FontFamily = new("Consolas"),
                FontSize = 12,
                TextWrapping = TextWrapping.NoWrap,
            };

            // Tokenize and colorize
            var tokens = Tokenize(line);
            foreach (var (text, type) in tokens)
            {
                var color = type switch
                {
                    "keyword" => LtaiTheme.AccentDNA,
                    "string" => LtaiTheme.AccentSystem,
                    "comment" => LtaiTheme.TextDim,
                    "number" => LtaiTheme.AccentInfo,
                    _ => LtaiTheme.TextPrimary,
                };
                tb.Inlines!.Add(new Run { Text = text, Foreground = LtaiTheme.Sbb(color) });
            }

            // If no tokens (empty line), add a space to preserve line height
            if (tb.Inlines == null || tb.Inlines.Count == 0)
                tb.Text = " ";

            stack.Children.Add(tb);
        }
        border.Child = stack;
    }

    private static List<(string text, string type)> Tokenize(string line)
    {
        var tokens = new List<(string, string)>();
        int i = 0;

        while (i < line.Length)
        {
            // Comments
            var cm = CommentRx.Match(line, i);
            if (cm.Success && cm.Index == i) { tokens.Add((cm.Value, "comment")); i = cm.Index + cm.Length; continue; }

            // Strings
            var sm = StringRx.Match(line, i);
            if (sm.Success && sm.Index == i) { tokens.Add((sm.Value, "string")); i = sm.Index + sm.Length; continue; }

            // Numbers
            var nm = NumberRx.Match(line, i);
            if (nm.Success && nm.Index == i) { tokens.Add((nm.Value, "number")); i = nm.Index + nm.Length; continue; }

            // Keywords
            var km = KeywordRx.Match(line, i);
            if (km.Success && km.Index == i)
            {
                var kw = km.Groups[1].Value;
                var isKeyword = KeywordSets.Values.Any(ks => ks.Contains(kw));
                tokens.Add((kw, isKeyword ? "keyword" : "text"));
                i = km.Index + km.Length;
                continue;
            }

            // Plain text
            var next = new[] { cm, sm, nm, km }
                .Where(m => m.Success && m.Index >= i)
                .Select(m => (int?)m.Index)
                .DefaultIfEmpty(null)
                .Min();

            if (next.HasValue && next.Value > i)
            { tokens.Add((line[i..next.Value], "text")); i = next.Value; }
            else if (next.HasValue) { i = next.Value; }
            else { tokens.Add((line[i..], "text")); break; }
        }

        return tokens;
    }

    public static void RenderSpan(string text, InlineCollection inlines)
    {
        int pos = 0;
        foreach (Match m in InlineFormatRx.Matches(text))
        {
            if (m.Index > pos)
                inlines.Add(Run(text[pos..m.Index]));

            if (m.Groups[1].Success)       // **bold**
                inlines.Add(Run(m.Groups[1].Value, weight: FontWeight.Bold));
            else if (m.Groups[2].Success)  // __bold__
                inlines.Add(Run(m.Groups[2].Value, weight: FontWeight.Bold));
            else if (m.Groups[3].Success)  // *italic*
                inlines.Add(Run(m.Groups[3].Value, italic: true));
            else if (m.Groups[4].Success)  // _italic_
                inlines.Add(Run(m.Groups[4].Value, italic: true));
            else if (m.Groups[5].Success)  // `code`
                inlines.Add(Run(m.Groups[5].Value, color: LtaiTheme.AccentInfo, font: "Consolas"));
            else if (m.Groups[6].Success)  // [link](url)
                inlines.Add(Run(m.Groups[6].Value, color: LtaiTheme.AccentInfo, italic: true));
            else if (m.Groups[8].Success)  // ~~strikethrough~~
                inlines.Add(Run(m.Groups[8].Value, color: LtaiTheme.TextDim));

            pos = m.Index + m.Length;
        }
        if (pos < text.Length)
            inlines.Add(Run(text[pos..]));
    }

    private static Run Run(string text, Color? color = null, FontWeight? weight = null,
        double? size = null, bool italic = false, string? font = null) => new()
    {
        Text = text,
        Foreground = LtaiTheme.Sbb(color ?? LtaiTheme.TextPrimary),
        FontWeight = weight ?? FontWeight.Normal,
        FontSize = size ?? 13,
        FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
        FontFamily = font != null ? new(font) : new("Consolas"),
    };

    // ─── Public helpers for ChatView code block syntax highlighting ───

    public static string[] GetKeywords(string? lang)
    {
        if (string.IsNullOrEmpty(lang)) return [];
        foreach (var (k, v) in KeywordSets)
            if (lang.Contains(k, StringComparison.OrdinalIgnoreCase)) return v;
        return [];
    }

    public static string ExtractCodeLang(string code)
    {
        var first = code.TrimStart();
        if (first.StartsWith("```")) { var end = first.IndexOf('\n', 3); return end > 3 ? first[3..end].Trim() : ""; }
        return "";
    }

    public static List<(string text, Color color)> TokenizeLine(string line, string[] keywords)
    {
        var tokens = new List<(string, Color)>();
        int i = 0;
        while (i < line.Length)
        {
            var cm = CommentRx.Match(line, i);
            if (cm.Success && cm.Index == i) { tokens.Add((cm.Value, LtaiTheme.TextDim)); i = cm.Index + cm.Length; continue; }

            var sm = StringRx.Match(line, i);
            if (sm.Success && sm.Index == i) { tokens.Add((sm.Value, LtaiTheme.AccentSystem)); i = sm.Index + sm.Length; continue; }

            var nm = NumberRx.Match(line, i);
            if (nm.Success && nm.Index == i) { tokens.Add((nm.Value, LtaiTheme.AccentInfo)); i = nm.Index + nm.Length; continue; }

            var km = KeywordRx.Match(line, i);
            if (km.Success && km.Index == i)
            {
                var kw = km.Groups[1].Value;
                var isKw = keywords.Contains(kw);
                tokens.Add((kw, isKw ? LtaiTheme.AccentDNA : LtaiTheme.TextPrimary));
                i = km.Index + km.Length;
                continue;
            }

            if (cm.Success && cm.Index > i) { tokens.Add((line[i..cm.Index], LtaiTheme.TextPrimary)); i = cm.Index; continue; }
            if (sm.Success && sm.Index > i) { tokens.Add((line[i..sm.Index], LtaiTheme.TextPrimary)); i = sm.Index; continue; }
            if (nm.Success && nm.Index > i) { tokens.Add((line[i..nm.Index], LtaiTheme.TextPrimary)); i = nm.Index; continue; }
            if (km.Success && km.Index > i) { tokens.Add((line[i..km.Index], LtaiTheme.TextPrimary)); i = km.Index; continue; }

            tokens.Add((line[i..], LtaiTheme.TextPrimary));
            break;
        }
        return tokens;
    }
}
