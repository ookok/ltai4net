using System.Text.Json;
using System.Text.RegularExpressions;

namespace LTAI.AI.Providers;

public static class RescueParser
{
    public static JsonElement? TryParseToolCall(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return null;

        return TryStandardParse(rawJson)
            ?? TryFixQuotes(rawJson)
            ?? TryFixBraces(rawJson)
            ?? TryExtractJsonBlock(rawJson)
            ?? TryFixTrailingComma(rawJson);
    }

    private static JsonElement? TryStandardParse(string json)
    {
        try { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
        catch { return null; }
    }

    private static readonly Regex s_quotedKeyPattern = new(@"'(\w+)'\s*:", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    private static readonly Regex s_unquotedKeyPattern = new(@"(?<=\{|,)\s*(\w+)\s*:", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    private static readonly Regex s_singleQuoteValuePattern = new(@":\s*'([^']*)'", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    private static readonly Regex s_unquotedValuePattern = new(@":\s*([^{\[\]"",}\s]+)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    private static JsonElement? TryFixQuotes(string json)
    {
        var fixed1 = s_quotedKeyPattern.Replace(json, "\"$1\":");
        var fixed2 = s_unquotedKeyPattern.Replace(fixed1, "\"$1\":");
        var fixed3 = s_singleQuoteValuePattern.Replace(fixed2, ": \"$1\"");
        var fixed4 = s_unquotedValuePattern.Replace(fixed3, m =>
        {
            var value = m.Groups[1].Value;
            if (value == "true" || value == "false" || value == "null"
                || double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _))
                return $": {value}";
            return $": \"{value}\"";
        });
        try { using var doc = JsonDocument.Parse(fixed4); return doc.RootElement.Clone(); }
        catch { return null; }
    }

    private static JsonElement? TryFixBraces(string json)
    {
        var trimmed = json.Trim();
        if (!trimmed.StartsWith('{') && trimmed.Contains('{'))
        {
            var start = trimmed.IndexOf('{');
            var end = trimmed.LastIndexOf('}');
            if (end > start)
                trimmed = trimmed[start..(end + 1)];
        }
        var openBraces = trimmed.Count(c => c == '{');
        var closeBraces = trimmed.Count(c => c == '}');
        if (openBraces > closeBraces)
            trimmed += new string('}', openBraces - closeBraces);
        else if (closeBraces > openBraces && trimmed.EndsWith('}'))
            trimmed = "{" + trimmed.TrimStart('{');

        try { using var doc = JsonDocument.Parse(trimmed); return doc.RootElement.Clone(); }
        catch { return null; }
    }

    private static JsonElement? TryExtractJsonBlock(string text)
    {
        var match = Regex.Match(text, @"\{[^{}]*(?:\{[^{}]*\}[^{}]*)*\}", RegexOptions.Singleline);
        if (match.Success)
            return TryFixQuotes(match.Value) ?? TryFixBraces(match.Value);
        return null;
    }

    private static JsonElement? TryFixTrailingComma(string json)
    {
        var fixed1 = Regex.Replace(json, @",\s*}", "}");
        var fixed2 = Regex.Replace(fixed1, @",\s*]", "]");
        try { using var doc = JsonDocument.Parse(fixed2); return doc.RootElement.Clone(); }
        catch { return null; }
    }
}
