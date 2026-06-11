using System.Text.Json;
using System.Text.RegularExpressions;

namespace LTAI.Agent.Context;

public static class ContentCompressor
{
    public enum ContentType { Json, Code, Text }

    private static readonly HashSet<char> _jsonStart = ['{', '['];

    private static readonly Regex _codePattern = new(
        @"^(#!|using |namespace |function |def |class |import |#include|fn |pub |impl |struct |enum |trait |interface |type |const |let |var |val )",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public static ContentType Detect(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return ContentType.Text;
        var trimmed = content.TrimStart();
        if (trimmed.Length > 0 && _jsonStart.Contains(trimmed[0]))
            return ContentType.Json;
        if (_codePattern.IsMatch(content))
            return ContentType.Code;
        return ContentType.Text;
    }

    public static string Compress(string content, ContentType? typeOverride = null)
    {
        var type = typeOverride ?? Detect(content);
        return type switch
        {
            ContentType.Json => CompressJson(content),
            ContentType.Code => CompressCode(content),
            _ => CompressText(content, 512)
        };
    }

    public static (string Compressed, string Summary) CompressWithSummary(string content, int maxTokens = 512)
    {
        var type = Detect(content);
        var summary = type switch
        {
            ContentType.Json => SummarizeJson(content),
            ContentType.Code => SummarizeCode(content),
            _ => TruncateText(content, 200)
        };
        var compressed = Compress(content, type);
        return (compressed, summary);
    }

    private static string CompressJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                MaxDepth = 64
            });
            return JsonSerializer.Serialize(doc.RootElement, _jsonOpts);
        }
        catch
        {
            return StripWhitespace(json);
        }
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static string SummarizeJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.ValueKind switch
            {
                JsonValueKind.Array => $"JSON array [{GetArraySummary(root)}]",
                JsonValueKind.Object => $"JSON object {{{GetObjectSummary(root)}}}",
                _ => "JSON value"
            };
        }
        catch
        {
            return $"JSON ({json.Length} chars)";
        }
    }

    private static string GetArraySummary(JsonElement arr)
    {
        var count = arr.GetArrayLength();
        if (count == 0) return "empty";
        var first = arr[0];
        var typeName = first.ValueKind switch
        {
            JsonValueKind.Object => first.EnumerateObject().FirstOrDefault().Name,
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            _ => "mixed"
        };
        return $"{count} items ({typeName}, \u2026)";
    }

    private static string GetObjectSummary(JsonElement obj)
    {
        var keys = obj.EnumerateObject().Select(p => p.Name).ToArray();
        if (keys.Length == 0) return "empty";
        return string.Join(", ", keys.Take(5)) + (keys.Length > 5 ? ", \u2026" : "");
    }

    private static readonly Regex _singleLineComment = new(@"//[^\n]*", RegexOptions.Compiled);
    private static readonly Regex _multiLineComment = new(@"/\*[\s\S]*?\*/", RegexOptions.Compiled);
    private static readonly Regex _blankLines = new(@"\n{3,}", RegexOptions.Compiled);
    private static readonly Regex _trailingWs = new(@"[ \t]+\n", RegexOptions.Compiled);

    private static string CompressCode(string code)
    {
        code = _singleLineComment.Replace(code, "");
        code = _multiLineComment.Replace(code, "");
        code = _blankLines.Replace(code, "\n\n");
        code = _trailingWs.Replace(code, "\n");

        var lines = code.Split('\n');
        if (lines.Length <= 400) return code.Trim();

        // Keep semantically important sections: method/function declarations,
        // class definitions, interfaces, plus brief head/tail context.
        var importantLines = new HashSet<int>();
        var sb = new System.Text.StringBuilder();

        // Always keep first 50 lines (imports/namespace/usings)
        for (int i = 0; i < Math.Min(50, lines.Length); i++)
            importantLines.Add(i);

        // Always keep last 30 lines (closing braces, tail logic)
        for (int i = Math.Max(0, lines.Length - 30); i < lines.Length; i++)
            importantLines.Add(i);

        // Scan for method/class/interface/struct/enum/trait declarations
        var declPattern = new System.Text.RegularExpressions.Regex(
            @"^\s*(public|private|protected|internal|static|virtual|override|abstract|sealed|async|unsafe|partial)?\s*" +
            @"(class|struct|interface|enum|record|trait|impl|fn|def|function|method|constructor|void|int|string|bool|var|Task|Task<|async\s+Task|IEnumerable|IAsyncEnumerable|ValueTask)\s+\w+\s*[\(<{]",
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.Compiled);

        foreach (System.Text.RegularExpressions.Match m in declPattern.Matches(code))
        {
            // Find the line number of this match
            var lineNum = code[..m.Index].Count(c => c == '\n');
            // Keep the declaration line + next 20 lines
            for (int i = lineNum; i < Math.Min(lineNum + 20, lines.Length); i++)
                importantLines.Add(i);
        }

        // Build compressed output
        int omitted = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (importantLines.Contains(i))
            {
                if (omitted > 0)
                {
                    sb.AppendLine($"--- [{omitted} lines omitted] ---");
                    omitted = 0;
                }
                sb.AppendLine(lines[i]);
            }
            else
            {
                omitted++;
            }
        }
        if (omitted > 0)
            sb.AppendLine($"--- [{omitted} lines omitted] ---");

        return sb.ToString().Trim();
    }

    private static string SummarizeCode(string code)
    {
        var lines = code.Split('\n');
        var nonEmpty = lines.Count(l => !string.IsNullOrWhiteSpace(l));
        var hasClass = code.Contains("class ") || code.Contains("struct ") || code.Contains("interface ");
        var hasFunc = code.Contains("fn ") || code.Contains("def ") || code.Contains("function ") || code.Contains("=>");
        var hasImport = code.Contains("using ") || code.Contains("import ") || code.Contains("#include");

        var features = new List<string>();
        if (hasClass) features.Add("class/struct");
        if (hasFunc) features.Add("functions");
        if (hasImport) features.Add("imports");

        return $"Code: {lines.Length} lines ({nonEmpty} non-empty), {string.Join(", ", features)}";
    }

    private static string CompressText(string text, int maxTokens)
    {
        var maxChars = maxTokens * 4;
        if (text.Length <= maxChars) return text;
        return TruncateText(text, maxChars);
    }

    private static string TruncateText(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        var head = text[..(maxChars / 2)];
        var tail = text[^(maxChars / 4)..];
        return $"{head}\n... [{text.Length - head.Length - tail.Length} chars omitted] ...\n{tail}";
    }

    private static string StripWhitespace(string json)
    {
        var sb = new System.Text.StringBuilder(json.Length);
        bool inString = false;
        for (int i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (c == '"') inString = !inString;
            if (!inString && char.IsWhiteSpace(c)) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
