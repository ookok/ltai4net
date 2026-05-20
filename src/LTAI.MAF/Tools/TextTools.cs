using System.ComponentModel;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace LTAI.MAF.Tools;

[Description("Text processing, encoding, hashing, and formatting tools")]
public sealed class TextTools
{
    [Description("Count characters, words, and lines in the given text.")]
    public static string CountText(
        [Description("Input text to analyze")] string text)
    {
        var chars = text.Length;
        var words = string.IsNullOrWhiteSpace(text) ? 0 : text.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;
        var lines = text.Count(c => c == '\n') + 1;
        return JsonSerializer.Serialize(new { characters = chars, words, lines });
    }

    [Description("Generate a hash of the input text using the specified algorithm (MD5, SHA1, SHA256, SHA384, SHA512).")]
    public static string HashText(
        [Description("Text to hash")] string text,
        [Description("Hash algorithm: MD5, SHA1, SHA256, SHA384, SHA512")] string algorithm = "SHA256")
    {
        byte[] hash = algorithm.ToUpperInvariant() switch
        {
            "MD5" => MD5.HashData(Encoding.UTF8.GetBytes(text)),
            "SHA1" => SHA1.HashData(Encoding.UTF8.GetBytes(text)),
            "SHA384" => SHA384.HashData(Encoding.UTF8.GetBytes(text)),
            "SHA512" => SHA512.HashData(Encoding.UTF8.GetBytes(text)),
            _ => SHA256.HashData(Encoding.UTF8.GetBytes(text))
        };
        return JsonSerializer.Serialize(new { algorithm, hash = Convert.ToHexStringLower(hash), length = text.Length });
    }

    [Description("Encode text to Base64 or decode Base64 back to text.")]
    public static string Base64Transform(
        [Description("Text to encode/decode")] string text,
        [Description("'encode' or 'decode'")] string operation)
    {
        try
        {
            if (operation.Equals("encode", StringComparison.OrdinalIgnoreCase))
            {
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
                return JsonSerializer.Serialize(new { operation = "encode", input = text[..Math.Min(text.Length, 100)], output = encoded });
            }
            else
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(text));
                return JsonSerializer.Serialize(new { operation = "decode", output = decoded });
            }
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [Description("Format a JSON string with proper indentation.")]
    public static string FormatJson(
        [Description("JSON string to format")] string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var formatted = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            return JsonSerializer.Serialize(new { formatted, length = formatted.Length });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"Invalid JSON: {ex.Message}" });
        }
    }

    [Description("Convert text between different case formats (upper, lower, title, camel, pascal, snake, kebab).")]
    public static string ConvertCase(
        [Description("Input text")] string text,
        [Description("Target case: upper, lower, title, camel, pascal, snake, kebab")] string targetCase)
    {
        var result = targetCase.ToLowerInvariant() switch
        {
            "upper" => text.ToUpperInvariant(),
            "lower" => text.ToLowerInvariant(),
            "title" => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text.ToLowerInvariant()),
            "snake" => string.Join("_", text.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant())),
            "kebab" => string.Join("-", text.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant())),
            "camel" => string.Concat(text.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries).Select((w, i) => i == 0 ? w.ToLowerInvariant() : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant())),
            "pascal" => string.Concat(text.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant())),
            _ => text
        };
        return JsonSerializer.Serialize(new { input = text[..Math.Min(text.Length, 200)], targetCase, result });
    }

    [Description("Search and replace text using regex pattern matching.")]
    public static string RegexReplace(
        [Description("Input text")] string text,
        [Description("Regular expression pattern")] string pattern,
        [Description("Replacement string")] string replacement)
    {
        try
        {
            var result = System.Text.RegularExpressions.Regex.Replace(text, pattern, replacement);
            var matches = System.Text.RegularExpressions.Regex.Matches(text, pattern).Count;
            return JsonSerializer.Serialize(new { pattern, replacement, matchesFound = matches, result = result[..Math.Min(result.Length, 5000)] });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [Description("Extract text matching a regex pattern.")]
    public static string RegexExtract(
        [Description("Input text to search")] string text,
        [Description("Regular expression pattern with capture groups")] string pattern)
    {
        try
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(text, pattern);
            var results = matches.Select(m => new
            {
                value = m.Value,
                groups = m.Groups.Values.Skip(1).Select(g => g.Value).ToList()
            }).Take(100).ToList();
            return JsonSerializer.Serialize(new { pattern, matchCount = matches.Count, matches = results });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [Description("Trim whitespace from the start, end, or both sides of text.")]
    public static string Trim(
        [Description("Input text")] string text,
        [Description("Trim mode: both, start, or end")] string mode = "both")
    {
        var result = mode.ToLowerInvariant() switch
        {
            "start" => text.TrimStart(),
            "end" => text.TrimEnd(),
            _ => text.Trim()
        };
        return JsonSerializer.Serialize(new { mode, originalLength = text.Length, resultLength = result.Length, result });
    }

    [Description("Concatenate multiple text strings with an optional separator.")]
    public static string Concat(
        [Description("JSON array of strings, e.g. [\"hello\", \"world\"]")] string partsJson,
        [Description("Separator between parts")] string? separator = null)
    {
        try
        {
            var parts = JsonSerializer.Deserialize<string[]>(partsJson);
            if (parts == null || parts.Length == 0)
                return JsonSerializer.Serialize(new { error = "No parts provided" });
            var result = string.Join(separator ?? "", parts);
            return JsonSerializer.Serialize(new { partsCount = parts.Length, separator, result });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }
}
