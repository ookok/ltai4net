using System.Text.RegularExpressions;

namespace LTAI.Core.System;

public static class TokenCounter
{
    public static int Estimate(string text)
    {
        if (string.IsNullOrEmpty(text)) return 1;

        var tokens = 0.0;
        var idx = 0;

        while (idx < text.Length)
        {
            var ch = text[idx];

            if (IsCjk(ch))
            {
                tokens += 1.5;
                idx++;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                idx++;
                continue;
            }

            if (char.IsDigit(ch) || char.IsPunctuation(ch))
            {
                tokens += 1.0;
                idx++;
                continue;
            }

            if (text[idx..].StartsWith("```"))
            {
                var endIdx = text.IndexOf("```", idx + 3, StringComparison.Ordinal);
                var codeEnd = endIdx >= 0 ? endIdx + 3 : text.Length;
                var codeLen = codeEnd - idx;
                tokens += Math.Max(1, codeLen / 3.0);
                idx = codeEnd;
                continue;
            }

            if (char.IsLetter(ch))
            {
                var wordStart = idx;
                while (idx < text.Length && (char.IsLetterOrDigit(text[idx]) || text[idx] == '\''))
                    idx++;
                tokens += 1.3;
                if (idx - wordStart > 8)
                    tokens += (idx - wordStart - 8) * 0.2;
                continue;
            }

            tokens += 1.0;
            idx++;
        }

        return Math.Max(1, (int)Math.Ceiling(tokens));
    }

    private static bool IsCjk(char ch)
    {
        return (ch >= 0x4E00 && ch <= 0x9FFF) ||
               (ch >= 0x3400 && ch <= 0x4DBF) ||
               (ch >= 0x2E80 && ch <= 0x2EFF) ||
               (ch >= 0x3000 && ch <= 0x303F) ||
               (ch >= 0xFF00 && ch <= 0xFFEF) ||
               (ch >= 0xFE30 && ch <= 0xFE4F) ||
               (ch >= 0xF900 && ch <= 0xFAFF) ||
               (ch >= 0x20000 && ch <= 0x2FFFF);
    }
}
