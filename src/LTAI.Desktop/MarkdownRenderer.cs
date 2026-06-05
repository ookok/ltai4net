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
    // Syntax highlighting: keyword patterns per language (expanded)
    private static readonly Dictionary<string, string[]> KeywordSets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["csharp"] = new[] { "class", "struct", "interface", "enum", "record", "namespace", "using",
            "public", "private", "protected", "internal", "static", "readonly", "virtual", "abstract",
            "override", "async", "await", "new", "return", "if", "else", "for", "foreach", "while",
            "do", "switch", "case", "break", "continue", "try", "catch", "finally", "throw",
            "var", "void", "int", "string", "bool", "double", "float", "long", "char", "object",
            "true", "false", "null", "this", "base", "in", "out", "ref", "is", "as", "typeof",
            "get", "set", "value", "where", "select", "from", "partial", "sealed", "required",
            "init", "global", "file", "checked", "unchecked", "unsafe", "fixed", "stackalloc",
            "params", "yield", "lock", "volatile", "event", "delegate", "implicit", "explicit",
            "operator", "sizeof", "nameof", "notnull", "unmanaged", "query", "group", "join",
            "let", "orderby", "descending", "ascending" },
        ["python"] = new[] { "class", "def", "return", "if", "elif", "else", "for", "while",
            "try", "except", "finally", "import", "from", "as", "with", "yield", "lambda",
            "True", "False", "None", "self", "and", "or", "not", "in", "is", "async", "await",
            "raise", "pass", "break", "continue", "global", "nonlocal", "match", "case",
            "type", "assert", "del", "print", "range", "len", "super", "property",
            "classmethod", "staticmethod", "dataclass", "enum", "list", "dict", "set", "tuple",
            "str", "int", "float", "bool", "any", "Optional", "Union", "Final", "override" },
        ["javascript"] = new[] { "function", "class", "const", "let", "var", "return", "if", "else",
            "for", "while", "do", "switch", "case", "break", "continue", "try", "catch", "finally",
            "throw", "new", "this", "async", "await", "import", "export", "default", "from",
            "true", "false", "null", "undefined", "typeof", "instanceof", "void", "delete",
            "yield", "super", "extends", "static", "get", "set", "of", "in", "with", "debugger" },
        ["typescript"] = new[] { "interface", "type", "enum", "namespace", "module", "declare",
            "abstract", "readonly", "public", "private", "protected", "static", "implements",
            "extends", "as", "is", "keyof", "typeof", "infer", "satisfies", "const", "let",
            "var", "function", "class", "return", "if", "else", "for", "while", "async", "await",
            "import", "export", "default", "from", "true", "false", "null", "undefined",
            "never", "unknown", "any", "void", "string", "number", "boolean", "symbol", "bigint" },
        ["go"] = new[] { "func", "type", "struct", "interface", "map", "chan", "go", "defer",
            "select", "range", "return", "if", "else", "for", "switch", "case", "break", "continue",
            "var", "const", "package", "import", "true", "false", "nil", "make", "new",
            "append", "len", "cap", "close", "delete", "panic", "recover", "error", "string",
            "int", "int64", "float64", "bool", "byte", "rune", "uintptr", "any", "comparable" },
        ["rust"] = new[] { "fn", "let", "mut", "const", "static", "pub", "use", "mod", "struct",
            "enum", "trait", "impl", "type", "ref", "match", "if", "else", "for", "while", "loop",
            "return", "break", "continue", "true", "false", "None", "Some", "Ok", "Err",
            "async", "await", "move", "unsafe", "extern", "dyn", "where", "as", "in",
            "self", "Self", "super", "crate", "let", "mut", "ref", "impl", "dyn" },
        ["java"] = new[] { "class", "interface", "enum", "record", "extends", "implements",
            "public", "private", "protected", "static", "final", "abstract", "synchronized",
            "volatile", "transient", "native", "strictfp", "return", "if", "else", "for",
            "while", "do", "switch", "case", "break", "continue", "try", "catch", "finally",
            "throw", "throws", "new", "this", "super", "import", "package", "true", "false",
            "null", "void", "int", "long", "double", "float", "boolean", "char", "byte",
            "short", "String", "var", "sealed", "permits", "instanceof", "var", "yield" },
    };

    private static readonly Regex OrderedListRx = new(@"^(\d+)\.\s");
    private static readonly Regex TableSepRx = new(@"^\|[\s\-:]+\|$");
    private static readonly Regex InlineFormatRx = new(
        @"\*\*(.+?)\*\*|__(.+?)__|\*(.+?)\*|_(.+?)_|`(.+?)`|\[(.+?)\]\(([^)]+)\)|~~(.+?)~~");

    public static InlineCollection Render(string text, InlineCollection inlines)
    {
        var lines = text.Split('\n');
        var inCode = false;
        var codeBuf = new System.Text.StringBuilder();

        for (int li = 0; li < lines.Length; li++)
        {
            var line = lines[li];
            if (li > 0 && !inCode) inlines.Add(new LineBreak());

            // 代码围栏由 ChatView.SplitCodeBlocks 预处理，此处不处理
            if (line.TrimStart().StartsWith("```"))
            {
                if (!inCode) { inCode = true; codeBuf.Clear(); continue; }
                else { inCode = false; codeBuf.Clear(); continue; }
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
                    if (ci > 0) inlines.Add(Run(" \u2502 ", color: LtaiTheme.TextDim, font: LtaiTheme.CodeFont));
                    RenderSpan(cells[ci], inlines);
                }
                continue;
            }
            RenderSpan(line, inlines);
        }
        return inlines;
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
                inlines.Add(Run(m.Groups[5].Value, color: LtaiTheme.AccentInfo, font: LtaiTheme.CodeFont));
            else if (m.Groups[6].Success)  // [link](url)
            {
                inlines.Add(Run(m.Groups[6].Value, color: LtaiTheme.AccentInfo));
                inlines.Add(Run($" ({m.Groups[7].Value})", color: LtaiTheme.TextDim, size: 11));
            }
            else if (m.Groups[8].Success)  // ~~strikethrough~~
                inlines.Add(Run(m.Groups[8].Value, color: LtaiTheme.TextDim));

            pos = m.Index + m.Length;
        }
        if (pos < text.Length)
            inlines.Add(Run(text[pos..]));
    }

    private static Run Run(string text, Color? color = null, FontWeight? weight = null,
        double? size = null, bool italic = false, FontFamily? font = null) => new()
    {
        Text = text,
        Foreground = LtaiTheme.Sbb(color ?? LtaiTheme.TextPrimary),
        FontWeight = weight ?? FontWeight.Normal,
        FontSize = size ?? 13,
        FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
        FontFamily = font ?? LtaiTheme.CodeFont,
    };

    // ─── Public helpers for ChatView code block syntax highlighting ───

    private static LTAI.Core.Rendering.TextMateHighlighter? _textMateHighlighter;

    private static LTAI.Core.Rendering.TextMateHighlighter GetHighlighter()
    {
        _textMateHighlighter ??= new LTAI.Core.Rendering.TextMateHighlighter();
        return _textMateHighlighter;
    }

    public static string[] GetKeywords(string? lang)
    {
        if (string.IsNullOrEmpty(lang)) return [];
        foreach (var (k, v) in KeywordSets)
            if (lang.Contains(k, StringComparison.OrdinalIgnoreCase)) return v;
        return [];
    }

    private static readonly Regex TokenRx = new(
        @"//[^\n]*|#[^\n]*                        # comment
        |""(?:[^""\\]+|\\.)*""                      # double-quoted string
        |'[^']*'                                    # single-quoted string
        |\b(\d+\.?\d*[fFlLdD]?)\b                   # number
        |\b([a-zA-Z_]\w*)\b                         # identifier
        |.                                          # any other char (fallback)
        ",
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    public static List<(string text, Color color)> TokenizeLine(string line, string[] keywords)
    {
        // Try TextMateSharp first for accurate highlighting
        var lang = keywords.Length > 0 ? "csharp" : ""; // approximate from context
        if (!string.IsNullOrEmpty(lang))
        {
            try
            {
                var hl = GetHighlighter();
                var tmTokens = hl.TokenizeLine(lang, line);
                if (tmTokens.Count > 0 && !(tmTokens.Count == 1 && tmTokens[0].fgColor == "#d4d4d4"))
                {
                    return tmTokens.Select(t =>
                    {
                        var c = Color.Parse(t.fgColor);
                        return (t.text, c);
                    }).ToList();
                }
            }
            catch { /* fall through to regex */ }
        }

        // Fallback: keyword regex
        var tokens = new List<(string, Color)>();
        var kws = keywords.Length > 0 ? new HashSet<string>(keywords, StringComparer.Ordinal) : null;

        foreach (Match m in TokenRx.Matches(line))
        {
            if (!m.Success) continue;

            if (m.Groups[1].Success)  // number
                tokens.Add((m.Value, LtaiTheme.AccentInfo));
            else if (m.Groups[2].Success)  // identifier (potential keyword)
            {
                var kw = m.Groups[2].Value;
                tokens.Add((kw, kws?.Contains(kw) == true ? LtaiTheme.AccentDNA : LtaiTheme.TextPrimary));
            }
            else if (m.Value.Length == 1 && (m.Value[0] == '/' || m.Value[0] == '#'))
                tokens.Add((m.Value, LtaiTheme.TextDim));
            else
                tokens.Add((m.Value, LtaiTheme.TextPrimary));
        }

        return tokens;
    }
}
