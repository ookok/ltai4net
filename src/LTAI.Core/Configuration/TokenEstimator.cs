using System.Text.RegularExpressions;

namespace LTAI.Core.Configuration;

/// <summary>
/// Language-aware token count estimator.
/// Detects CJK-dominant text and applies a per-character token ratio.
/// Rough ratios:
///   Latin: ~4 chars/token (0.25 tokens per char)
///   CJK:   ~1.5 chars/token (0.67 tokens per char)
///   Mixed: weighted by CJK ratio in text
/// Compared to naive x/4 estimate, correctly scales CJK-heavy inputs ~2.6x higher.
/// </summary>
public static partial class TokenEstimator
{
    private const double LatinTokenPerChar = 0.25;
    private const double CjkTokenPerChar = 0.67;

    [GeneratedRegex(@"\p{IsCJKUnifiedIdeographs}|\p{IsCJKCompatibilityIdeographs}|\p{IsHiragana}|\p{IsKatakana}|\p{IsCJKSymbolsandPunctuation}", RegexOptions.Compiled)]
    private static partial Regex CjkRegex();

    public static int Estimate(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var cjkCount = CjkRegex().Matches(text).Count;
        var totalChars = text.Length;
        if (totalChars == 0) return 0;
        var cjkRatio = (double)cjkCount / totalChars;
        var tokenPerChar = LatinTokenPerChar + (CjkTokenPerChar - LatinTokenPerChar) * cjkRatio;
        return Math.Max(1, (int)(totalChars * tokenPerChar));
    }

    /// <summary>Estimate tokens for a list of messages.</summary>
    public static int EstimateTotal(IEnumerable<string> messages)
    {
        var sum = 0;
        foreach (var msg in messages)
        {
            if (!string.IsNullOrEmpty(msg))
                sum += Estimate(msg);
        }
        return sum;
    }
}
