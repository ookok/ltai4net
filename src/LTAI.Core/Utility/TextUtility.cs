using System.Text.RegularExpressions;

namespace LTAI.Core.Utility;

public static class TextUtility
{
    public static HashSet<string> Tokenize(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in Regex.Matches(text, @"[\u4e00-\u9fff]+|[a-zA-Z]+"))
        {
            var token = match.Value.ToLowerInvariant();
            if (token.Length >= 2)
                tokens.Add(token);
        }

        return tokens;
    }

    public static double JaccardSimilarity(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return 0.0;

        var intersection = a.Count(x => b.Contains(x));
        var union = a.Count + b.Count - intersection;
        return union > 0 ? (double)intersection / union : 0.0;
    }

    public static double JaccardSimilarity(string a, string b)
    {
        return JaccardSimilarity(Tokenize(a), Tokenize(b));
    }

    public static string TruncateSnippet(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (text.Length <= maxLen) return text;
        return text[..(maxLen - 3)] + "...";
    }
}
