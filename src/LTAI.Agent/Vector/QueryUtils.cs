using System.Text.RegularExpressions;

namespace LTAI.Agent.Vector;

/// <summary>
/// Shared query utility methods for knowledge and code graph retrieval.
/// Eliminates duplication between KbGraph and CgGraph.
/// </summary>
public static partial class QueryUtils
{
    /// <summary>
    /// L0 short-circuit: simple queries don't trigger LLM rewrite.
    /// Conditions: ≤4 words, no special symbols, no code markers.
    /// </summary>
    public static bool IsSimpleQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 50) return false;
        var wordCount = query.Split([' ', '，', '。', '、'], StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount > 4) return false;
        // Contains code special characters → needs LLM
        if (query.Any(c => c is '_' or '.' or '/' or '\\' or '(' or ')' or '[' or ']' or '<' or '>'))
            return false;
        return true;
    }

    /// <summary>
    /// Detect if query contains code patterns (C#, Python, JS, etc.).
    /// Used by IsKnowledgeQuery to force KG lookup for code-related queries.
    /// </summary>
    public static bool ContainsCodePattern(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        // C# language keywords
        var codePatterns = new[]
        {
            "async", "await", "Task<", "Task.", "IEnumerable", "IQueryable",
            "namespace ", "class ", "interface ", "struct ", "enum ", "record ",
            "void ", "int ", "string ", "bool ", "var ", "new ", "null ",
            "=>", "::", "??", "?.", "??=",
            ".cs", ".csproj", ".sln",
            "HttpClient", "HttpResponse", "IActionResult",
            "ConfigureAwait", "GetAwaiter", "ValueTask",
            "List<", "Dictionary<", "HashSet<", "Concurrent",
            "public ", "private ", "protected ", "internal ", "static ",
            "readonly", "virtual", "override", "abstract", "sealed",
            "partial", "ref ", "out ", "in ", "params",
            // Python
            "def ", "class ", "import ", "from ", "self.", "async def",
            // JavaScript/TypeScript
            "function ", "const ", "let ", "export ", "import ", "Promise",
            // Go
            "func ", "goroutine", "chan ", "interface{",
            // Rust
            "fn ", "impl ", "trait ", "mut ", "unwrap()",
        };

        if (codePatterns.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Contains paired parentheses with length > 10 (function call syntax)
        if (text.Length > 10)
        {
            int open = 0, close = 0;
            foreach (var c in text) { if (c == '(') open++; if (c == ')') close++; }
            if (open >= 2 && close >= 2) return true;
        }

        return false;
    }

    /// <summary>
    /// Sanitize FTS5 query to prevent syntax errors.
    /// Removes unbalanced parens, bare @. Preserves ^ * " + - ~ : for power users.
    /// </summary>
    public static string SanitizeFts5Query(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return query;

        var sanitized = Fts5SpecialChars().Replace(query, " ");
        sanitized = Regex.Replace(sanitized, @"\s+", " ").Trim();

        // Cap query length so that 100 KB paste doesn't choke FTS5
        const int MaxQueryLength = 500;
        if (sanitized.Length > MaxQueryLength)
            sanitized = sanitized[..MaxQueryLength];

        return sanitized.Length > 0 ? sanitized : query;
    }

    [GeneratedRegex(@"[()@]")]
    private static partial Regex Fts5SpecialChars();

    /// <summary>
    /// Expand CJK text for FTS5 search: Chinese queries need character-level
    /// processing since the standard porter/unicode61 tokenizer doesn't split CJK.
    /// Splits CJK text into overlapping bigrams for better recall.
    /// Example: "用户登录" → "用户 户登 登录"
    /// </summary>
    public static string ExpandCjkQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return query;

        var result = new System.Text.StringBuilder(query.Length * 2);
        var cjkBuffer = new System.Text.StringBuilder();

        foreach (var c in query)
        {
            if (IsCjkCharacter(c))
            {
                cjkBuffer.Append(c);
            }
            else
            {
                // Flush any accumulated CJK text
                if (cjkBuffer.Length > 0)
                {
                    result.Append(ExpandCjkSegment(cjkBuffer.ToString()));
                    cjkBuffer.Clear();
                }
                result.Append(c);
            }
        }

        // Flush remaining CJK text
        if (cjkBuffer.Length > 0)
            result.Append(ExpandCjkSegment(cjkBuffer.ToString()));

        return result.ToString();
    }

    /// <summary>Check if a character is CJK (Chinese/Japanese/Korean).</summary>
    private static bool IsCjkCharacter(char c)
    {
        var cat = char.GetUnicodeCategory(c);
        return cat == System.Globalization.UnicodeCategory.OtherLetter
            || (c >= 0x3000 && c <= 0x30FF)   // CJK Symbols + Japanese
            || (c >= 0x4E00 && c <= 0x9FFF)   // CJK Unified Ideographs
            || (c >= 0xAC00 && c <= 0xD7AF);  // Korean Hangul
    }

    /// <summary>Expand a CJK character segment into overlapping bigrams.</summary>
    private static string ExpandCjkSegment(string segment)
    {
        if (segment.Length <= 1) return segment + " ";
        var parts = new List<string>();
        // Single characters
        foreach (var c in segment)
            parts.Add(c.ToString());
        // Overlapping bigrams
        for (int i = 0; i < segment.Length - 1; i++)
            parts.Add(segment.Substring(i, 2));
        return " " + string.Join(" ", parts) + " ";
    }
}
