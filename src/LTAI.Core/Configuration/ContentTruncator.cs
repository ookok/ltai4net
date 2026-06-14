using System.Text.RegularExpressions;

namespace LTAI.Core.Configuration;

/// <summary>
/// Structural-aware content truncation that preserves JSON/XML/HTML boundary integrity.
/// Unlike raw text[..maxChars], this finds the last valid structural terminator
/// (closing brace, bracket, tag) before the cutoff to avoid malformed responses.
/// </summary>
public static partial class ContentTruncator
{
    [GeneratedRegex(@"^[{\[]", RegexOptions.Compiled)]
    private static partial Regex LooksLikeJsonRegex();

    [GeneratedRegex(@"^<\w+", RegexOptions.Compiled)]
    private static partial Regex LooksLikeXmlRegex();

    [GeneratedRegex(@"^<html|^<!DOCTYPE\s+html", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LooksLikeHtmlRegex();

    /// <summary>
    /// Truncate content preserving structural integrity.
    /// Detects JSON/XML/HTML and truncates at the last valid boundary.
    /// Falls back to sentence-boundary truncation for plain text.
    /// </summary>
    /// <param name="content">Content to potentially truncate.</param>
    /// <param name="maxChars">Maximum character count. If content is shorter, returned as-is.</param>
    /// <returns>Truncated content with truncation notice appended.</returns>
    public static string Truncate(string content, int maxChars)
    {
        if (content.Length <= maxChars) return content;

        var truncated = TruncateCore(content, maxChars);
        var remaining = content.Length - truncated.Length;
        return $"{truncated}\n... [truncated at {maxChars} chars, {remaining} more chars]";
    }

    private static string TruncateCore(string content, int maxChars)
    {
        var head = content.Length > 200 ? content[..200].TrimStart() : content.TrimStart();

        if (LooksLikeJsonRegex().IsMatch(head))
            return TruncateJson(content, maxChars);
        if (LooksLikeHtmlRegex().IsMatch(head))
            return TruncateHtml(content, maxChars);
        if (LooksLikeXmlRegex().IsMatch(head))
            return TruncateXml(content, maxChars);

        return TruncatePlainText(content, maxChars);
    }

    private static string TruncateJson(string content, int maxChars)
    {
        var window = content[..Math.Min(maxChars, content.Length)];
        // Walk backwards from the limit to find a valid JSON boundary
        int depth = 0;
        bool inString = false;
        int lastValidEnd = 0;
        for (int i = 0; i < window.Length; i++)
        {
            char c = window[i];
            if (inString)
            {
                if (c == '"' && (i == 0 || window[i - 1] != '\\'))
                    inString = false;
            }
            else
            {
                switch (c)
                {
                    case '"': inString = true; break;
                    case '{': case '[': depth++; break;
                    case '}': case ']': depth--; if (depth == 0) lastValidEnd = i + 1; break;
                }
            }
        }

        if (lastValidEnd > 0 && lastValidEnd >= maxChars / 2)
            return content[..lastValidEnd];

        // Fallback: find last comma or newline before limit
        var lastComma = content.LastIndexOf(',', maxChars - 1);
        if (lastComma > maxChars / 2)
            return content[..(lastComma + 1)];

        return TruncatePlainText(content, maxChars);
    }

    private static string TruncateXml(string content, int maxChars)
    {
        var window = content[..Math.Min(maxChars, content.Length)];
        var lastClosingTag = window.LastIndexOf("</");
        if (lastClosingTag > maxChars / 3)
        {
            var endOfTag = content.IndexOf('>', lastClosingTag);
            if (endOfTag > 0 && endOfTag < maxChars + 100)
                return content[..(endOfTag + 1)];
        }

        // Fallback: find last complete self-closing or closing tag
        for (int i = maxChars - 1; i >= maxChars / 3; i--)
        {
            if (window[i] == '>' && i > 0 && (window[i - 1] == '/' || window[i - 1] == '"' || window[i - 1] == '\''))
                return content[..(i + 1)];
        }

        return TruncatePlainText(content, maxChars);
    }

    private static string TruncateHtml(string content, int maxChars)
    {
        var window = content[..Math.Min(maxChars, content.Length)];
        // Single reverse scan for the latest HTML block closing tag
        var blockTags = new[] { "</div>", "</table>", "</pre>", "</ul>", "</ol>", "</section>", "</article>", "</tr>", "</p>", "</li>", "</body>", "</html>" };
        for (int i = window.Length - 1; i >= maxChars / 3; i--)
        {
            if (window[i] != '>') continue;
            foreach (var tag in blockTags)
            {
                var tagLen = tag.Length;
                if (i >= tagLen - 1 && window.AsSpan(i - tagLen + 1, tagLen).SequenceEqual(tag.AsSpan()))
                    return content[..(i + 1)];
            }
        }
        return TruncateXml(content, maxChars);
    }

    private static string TruncatePlainText(string content, int maxChars)
    {
        // Truncate at last sentence boundary (., !, ?, newline) before the limit
        var window = content[..Math.Min(maxChars, content.Length)];
        for (int i = maxChars - 1; i >= maxChars / 2; i--)
        {
            char c = window[i];
            if (c == '.' || c == '!' || c == '?' || c == '\n')
            {
                // Check that this looks like a sentence boundary
                if (i + 1 < window.Length && (window[i + 1] == ' ' || window[i + 1] == '\n' || window[i + 1] == '\r'))
                    return content[..(i + 1)];
                if (c == '\n')
                    return content[..i];
            }
        }
        return content[..maxChars];
    }
}
