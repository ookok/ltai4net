using System.Text;
using System.Text.RegularExpressions;
using LTAI.Document.Interfaces;
using LTAI.Document.Models;

namespace LTAI.Document.Parsers;

public sealed class MarkdownParser : IDocumentParser
{
    public string FormatName => "Markdown";
    public IReadOnlyList<string> SupportedExtensions => new[] { ".md", ".markdown" };

    public bool CanParse(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext is ".md" or ".markdown";
    }

    public async Task<ParseResult> ParseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken);
        var metadata = new Dictionary<string, string>
        {
            ["size"] = new FileInfo(filePath).Length.ToString(),
            ["lines"] = text.Count(c => c == '\n').ToString(),
            ["chars"] = text.Length.ToString()
        };

        var headings = Regex.Matches(text, @"^#{1,6}\s+(.+)$", RegexOptions.Multiline);
        metadata["headings"] = headings.Count.ToString();
        if (headings.Count > 0)
            metadata["top_heading"] = headings[0].Groups[1].Value.Trim();

        var links = Regex.Matches(text, @"\[([^\]]+)\]\(([^\)]+)\)");
        metadata["links"] = links.Count.ToString();

        var codeBlocks = Regex.Matches(text, @"```[\s\S]*?```");
        metadata["code_blocks"] = codeBlocks.Count.ToString();

        var images = Regex.Matches(text, @"!\[([^\]]*)\]\(([^\)]+)\)");
        metadata["images"] = images.Count.ToString();

        var tables = new List<Dictionary<string, object?>>();
        var tableMatches = Regex.Matches(text, @"^\|(.+)\|\s*$[\r\n]+\|[-:\|\s]+\|[\r\n]+((?:\|.+\|\s*[\r\n]+)+)", RegexOptions.Multiline);
        foreach (Match tm in tableMatches)
        {
            var headerRow = tm.Groups[1].Value.Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(h => h.Trim()).ToArray();
            var dataRows = tm.Groups[2].Value.TrimEnd('\r', '\n').Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var row in dataRows)
            {
                var cols = row.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()).ToArray();
                var dict = new Dictionary<string, object?>();
                for (int i = 0; i < Math.Min(headerRow.Length, cols.Length); i++)
                    dict[headerRow[i]] = cols[i];
                tables.Add(dict);
            }
        }
        metadata["tables"] = tables.Count.ToString();

        var plainText = Regex.Replace(text, @"```[\s\S]*?```", "[code]");
        plainText = Regex.Replace(plainText, @"`([^`]+)`", "$1");
        plainText = Regex.Replace(plainText, @"\*\*([^*]+)\*\*", "$1");
        plainText = Regex.Replace(plainText, @"\*([^*]+)\*", "$1");
        plainText = Regex.Replace(plainText, @"!\[([^\]]*)\]\([^\)]+\)", "[img: $1]");
        plainText = Regex.Replace(plainText, @"\[([^\]]+)\]\([^\)]+\)", "$1");
        plainText = Regex.Replace(plainText, @"^#{1,6}\s+", "", RegexOptions.Multiline);
        plainText = Regex.Replace(plainText, @"^\s*[-*+]\s+", "  - ", RegexOptions.Multiline);
        plainText = Regex.Replace(plainText, @"^\s*\d+\.\s+", "  - ", RegexOptions.Multiline);

        return new ParseResult
        {
            FilePath = filePath, Format = "markdown", Success = true,
            ParserUsed = "markdown", Text = plainText, Tables = tables, Metadata = metadata
        };
    }
}

public sealed class IniConfigParser : IDocumentParser
{
    public string FormatName => "INI Config";
    public IReadOnlyList<string> SupportedExtensions => new[] { ".ini", ".cfg", ".conf", ".config", ".properties" };

    public bool CanParse(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext is ".ini" or ".cfg" or ".conf" or ".config" or ".properties";
    }

    public async Task<ParseResult> ParseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken);
        var metadata = new Dictionary<string, string>
        {
            ["size"] = new FileInfo(filePath).Length.ToString()
        };

        var sections = new Dictionary<string, Dictionary<string, string>>();
        var currentSection = "";

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
                continue;

            var sectionMatch = Regex.Match(trimmed, @"^\[(.+)\]$");
            if (sectionMatch.Success)
            {
                currentSection = sectionMatch.Groups[1].Value.Trim();
                sections.TryAdd(currentSection, new());
                continue;
            }

            var kvMatch = Regex.Match(trimmed, @"^([^=:]+)[=:]\s*(.*)$");
            if (kvMatch.Success)
            {
                if (string.IsNullOrEmpty(currentSection))
                {
                    currentSection = "global";
                    sections.TryAdd(currentSection, new());
                }
                sections[currentSection][kvMatch.Groups[1].Value.Trim()] = kvMatch.Groups[2].Value.Trim();
            }
        }

        metadata["sections"] = sections.Count.ToString();
        metadata["total_keys"] = sections.Values.Sum(s => s.Count).ToString();

        var plainText = new StringBuilder();
        foreach (var (section, keys) in sections)
        {
            plainText.AppendLine($"[{section}]");
            foreach (var (k, v) in keys)
                plainText.AppendLine($"{k} = {v}");
            plainText.AppendLine();
        }

        var tables = new List<Dictionary<string, object?>>();
        foreach (var (section, keys) in sections)
        {
            var row = new Dictionary<string, object?> { ["section"] = section };
            foreach (var (k, v) in keys) row[k] = v;
            tables.Add(row);
        }

        return new ParseResult
        {
            FilePath = filePath, Format = "ini", Success = true,
            ParserUsed = "ini", Text = plainText.ToString(), Tables = tables, Metadata = metadata
        };
    }
}

public sealed partial class YamlTomlParser : IDocumentParser
{
    public string FormatName => "YAML/TOML";
    public IReadOnlyList<string> SupportedExtensions => new[] { ".yaml", ".yml", ".toml" };

    public bool CanParse(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext is ".yaml" or ".yml" or ".toml";
    }

    public async Task<ParseResult> ParseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken);
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var isToml = ext == ".toml";
        var format = isToml ? "toml" : "yaml";

        var metadata = new Dictionary<string, string>
        {
            ["size"] = new FileInfo(filePath).Length.ToString(),
            ["lines"] = text.Count(c => c == '\n').ToString()
        };

        var structure = ParseStructure(text, isToml);
        metadata["top_keys"] = structure.Count.ToString();

        var tables = new List<Dictionary<string, object?>>();
        FlattenToTable(structure, "", tables);

        return new ParseResult
        {
            FilePath = filePath, Format = format, Success = true,
            ParserUsed = format, Text = text, Tables = tables, Metadata = metadata
        };
    }

    private static Dictionary<string, object?> ParseStructure(string text, bool isToml)
    {
        var result = new Dictionary<string, object?>();
        var lines = text.Split('\n');
        var stack = new Stack<(int indent, Dictionary<string, object?> dict)>();
        stack.Push((0, result));
        string? lastArrayKey = null;
        var currentArray = new List<object>();

        foreach (var rawLine in lines)
        {
            var line = rawLine;
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                continue;

            var indent = line.Length - line.TrimStart().Length;

            if (isToml && Regex.IsMatch(line.TrimStart(), @"^\[\[?([^\]]+)\]\]?$"))
            {
                var sectionMatch = Regex.Match(line.TrimStart(), @"^\[\[?([^\]]+)\]\]?$");
                var section = sectionMatch.Groups[1].Value.Trim();
                var parts = section.Split('.');
                var current = result;
                for (int i = 0; i < parts.Length; i++)
                {
                    if (i == parts.Length - 1)
                    {
                        if (!current.TryGetValue(parts[i], out var val) || val is not Dictionary<string, object?>)
                            current[parts[i]] = new Dictionary<string, object?>();
                    }
                    else
                    {
                        current.TryAdd(parts[i], new Dictionary<string, object?>());
                        current = (Dictionary<string, object?>)current[parts[i]]!;
                    }
                }
                continue;
            }

            // Handle list items
            var listMatch = Regex.Match(line.TrimStart(), @"^\s*-\s+(.+)");
            if (listMatch.Success)
            {
                var val = ParseScalar(listMatch.Groups[1].Value.Trim());
                currentArray.Add(val);
                if (lastArrayKey != null && stack.Peek().dict.TryGetValue(lastArrayKey, out _))
                    stack.Peek().dict[lastArrayKey] = new List<object>(currentArray);
                continue;
            }

            // Reset array context
            if (currentArray.Count > 0)
            {
                currentArray.Clear();
                lastArrayKey = null;
            }

            // Key-value pairs
            var kvMatch = Regex.Match(line.TrimStart(), isToml
                ? @"^([\w._-]+)\s*=\s*(.*)"
                : @"^([\w._-]+)\s*:\s*(.*)");
            if (!kvMatch.Success) continue;

            var key = kvMatch.Groups[1].Value.Trim();
            var value = kvMatch.Groups[2].Value.Trim();

            // Pop stack based on indent
            while (stack.Count > 1 && stack.Peek().indent >= indent)
                stack.Pop();

            var target = stack.Peek().dict;

            if (string.IsNullOrEmpty(value) || value == "|" || value == ">" || value == "|-")
            {
                var nested = new Dictionary<string, object?>();
                target[key] = nested;
                stack.Push((indent, nested));
            }
            else if (value == "[]")
            {
                lastArrayKey = key;
                currentArray.Clear();
                target[key] = currentArray;
            }
            else
            {
                target[key] = ParseScalar(value);
            }
        }

        return result;
    }

    private static object ParseScalar(string value)
    {
        value = value.Trim().Trim('"', '\'');
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        if (value.Equals("null", StringComparison.OrdinalIgnoreCase) || value.Equals("~")) return null!;
        if (double.TryParse(value, out var d)) return d;
        if (int.TryParse(value, out var i)) return i;
        return value;
    }

    private static void FlattenToTable(Dictionary<string, object?> dict, string prefix,
        List<Dictionary<string, object?>> tables)
    {
        var row = new Dictionary<string, object?>();
        foreach (var (k, v) in dict)
        {
            var key = string.IsNullOrEmpty(prefix) ? k : $"{prefix}.{k}";
            if (v is Dictionary<string, object?> nested)
                FlattenToTable(nested, key, tables);
            else if (v is List<object> list)
                row[key] = string.Join(", ", list.Select(o => o?.ToString() ?? ""));
            else
                row[key] = v;
        }
        if (row.Count > 0) tables.Add(row);
    }
}

public sealed class HtmlTextParser : IDocumentParser
{
    public string FormatName => "HTML";
    public IReadOnlyList<string> SupportedExtensions => new[] { ".html", ".htm" };

    public bool CanParse(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext is ".html" or ".htm";
    }

    public async Task<ParseResult> ParseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var html = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken);
        var metadata = new Dictionary<string, string>
        {
            ["size"] = new FileInfo(filePath).Length.ToString(),
            ["html_chars"] = html.Length.ToString()
        };

        var title = "";
        var titleMatch = Regex.Match(html, @"<title[^>]*>([^<]+)</title>", RegexOptions.IgnoreCase);
        if (titleMatch.Success) title = titleMatch.Groups[1].Value.Trim();

        // Extract metadata
        foreach (Match m in Regex.Matches(html, @"<meta\s+name=""([^""]+)""\s+content=""([^""]+)""", RegexOptions.IgnoreCase))
            metadata[$"meta_{m.Groups[1].Value}"] = m.Groups[2].Value;

        // Extract links
        var links = new List<string>();
        foreach (Match m in Regex.Matches(html, @"<a[^>]+href=""([^""]+)""[^>]*>([^<]*)</a>", RegexOptions.IgnoreCase))
            links.Add($"{m.Groups[2].Value.Trim()} -> {m.Groups[1].Value.Trim()}");
        metadata["links"] = links.Count.ToString();

        // Convert to plain text
        var text = html;
        text = Regex.Replace(text, @"<script[^>]*>[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<style[^>]*>[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<noscript[^>]*>[\s\S]*?</noscript>", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<!--[\s\S]*?-->", "");
        text = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</?(p|div|section|article|h[1-6]|li|tr)[^>]*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", "");
        text = Regex.Replace(text, @"&nbsp;", " ");
        text = Regex.Replace(text, @"&amp;", "&");
        text = Regex.Replace(text, @"&lt;", "<");
        text = Regex.Replace(text, @"&gt;", ">");
        text = Regex.Replace(text, @"&quot;", "\"");
        text = Regex.Replace(text, @"&#\d+;", "");
        text = Regex.Replace(text, @"\n\s*\n", "\n\n");
        text = text.Trim();

        if (!string.IsNullOrEmpty(title))
            metadata["title"] = title;

        return new ParseResult
        {
            FilePath = filePath, Format = "html", Success = true,
            ParserUsed = "html", Text = text, Metadata = metadata
        };
    }
}

public sealed class LogParser : IDocumentParser
{
    public string FormatName => "Log File";
    public IReadOnlyList<string> SupportedExtensions => new[] { ".log" };

    public bool CanParse(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext == ".log";
    }

    public async Task<ParseResult> ParseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var metadata = new Dictionary<string, string>
        {
            ["size"] = new FileInfo(filePath).Length.ToString(),
            ["lines"] = lines.Length.ToString()
        };

        var errorCount = Regex.Matches(text, @"(?i)(error|exception|fatal|critical|fail|crash)").Count;
        var warnCount = Regex.Matches(text, @"(?i)(warn|warning|deprecated)").Count;
        var infoCount = Regex.Matches(text, @"(?i)(info|information|debug|trace)").Count;

        metadata["errors"] = errorCount.ToString();
        metadata["warnings"] = warnCount.ToString();
        metadata["infos"] = infoCount.ToString();

        // Detect timestamps
        var tsCount = Regex.Matches(text, @"\d{4}[-/]\d{2}[-/]\d{2}").Count;
        metadata["timestamps"] = tsCount.ToString();

        // Extract log level distribution
        var levels = new[] { "ERROR", "WARN", "INFO", "DEBUG", "TRACE", "FATAL", "CRITICAL" };
        foreach (var level in levels)
        {
            var count = Regex.Matches(text, $@"\b{level}\b").Count;
            if (count > 0) metadata[$"level_{level.ToLower()}"] = count.ToString();
        }

        return new ParseResult
        {
            FilePath = filePath, Format = "log", Success = true,
            ParserUsed = "log", Text = text, Metadata = metadata
        };
    }
}
