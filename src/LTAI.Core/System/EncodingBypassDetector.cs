using System.Text;
using System.Text.RegularExpressions;

namespace LTAI.Core.System;

public sealed class EncodingBypassDetector
{
    private static readonly Dictionary<string, string> LeetMap = new()
    {
        ["4"] = "a", ["@"] = "a", ["3"] = "e", ["1"] = "i", ["!"] = "i",
        ["0"] = "o", ["$"] = "s", ["5"] = "s", ["7"] = "t", ["+"] = "t",
        ["("] = "c", ["|)"] = "d", ["|="] = "f", ["9"] = "g", ["#"] = "h",
        ["|<"] = "k", ["|_"] = "l", ["|\\/|"] = "m", ["|\\|"] = "n",
        ["|2"] = "r", ["|_|"] = "u", ["\\/"] = "v", ["\\/\\/"] = "w",
        ["><"] = "x", ["`/"] = "y", ["2"] = "z"
    };

    private static readonly Dictionary<char, char> HomoglyphMap = new()
    {
        ['à'] = 'a', ['á'] = 'a', ['â'] = 'a', ['ã'] = 'a', ['ä'] = 'a',
        ['å'] = 'a', ['α'] = 'a', ['а'] = 'a',
        ['è'] = 'e', ['é'] = 'e', ['ê'] = 'e', ['ë'] = 'e', ['е'] = 'e',
        ['ì'] = 'i', ['í'] = 'i', ['î'] = 'i', ['ï'] = 'i', ['і'] = 'i',
        ['ò'] = 'o', ['ó'] = 'o', ['ô'] = 'o', ['õ'] = 'o', ['ö'] = 'o',
        ['о'] = 'o', ['ο'] = 'o',
        ['ù'] = 'u', ['ú'] = 'u', ['û'] = 'u', ['ü'] = 'u',
        ['с'] = 'c', ['р'] = 'p', ['х'] = 'x', ['у'] = 'y',
        ['Α'] = 'A', ['Β'] = 'B', ['Ε'] = 'E', ['Ζ'] = 'Z',
        ['Η'] = 'H', ['Ι'] = 'I', ['Κ'] = 'K', ['Μ'] = 'M',
        ['Ν'] = 'N', ['Ο'] = 'O', ['Ρ'] = 'P', ['Τ'] = 'T',
        ['Υ'] = 'Y', ['Χ'] = 'X',
        ['０'] = '0', ['１'] = '1', ['２'] = '2', ['３'] = '3', ['４'] = '4',
        ['５'] = '5', ['６'] = '6', ['７'] = '7', ['８'] = '8', ['９'] = '9'
    };

    public List<DecodedVariant> Decode(string text)
    {
        var variants = new List<DecodedVariant>();

        var base64 = TryDecodeBase64(text);
        if (base64 != null) variants.Add(base64);

        var rot13 = TryDecodeRot13(text);
        if (rot13 != null) variants.Add(rot13);

        var url = TryDecodeUrl(text);
        if (url != null) variants.Add(url);

        var hex = TryDecodeHex(text);
        if (hex != null) variants.Add(hex);

        var leet = TryDecodeLeetspeak(text);
        if (leet != null) variants.Add(leet);

        var homoglyph = TryDecodeHomoglyphs(text);
        if (homoglyph != null) variants.Add(homoglyph);

        var unicode = TryDecodeUnicode(text);
        if (unicode != null) variants.Add(unicode);

        return variants;
    }

    public string DecodeAllAndJoin(string text)
    {
        var decoded = Decode(text);
        var parts = new List<string> { text };
        foreach (var v in decoded)
        {
            if (v.Decoded != text && !string.IsNullOrWhiteSpace(v.Decoded))
                parts.Add(v.Decoded);
        }
        return string.Join(" ", parts);
    }

    private static DecodedVariant? TryDecodeBase64(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length % 4 != 0 || trimmed.Length < 4) return null;
        if (!Regex.IsMatch(trimmed, @"^[A-Za-z0-9+/=]+$")) return null;

        try
        {
            var bytes = Convert.FromBase64String(trimmed);
            var decoded = Encoding.UTF8.GetString(bytes);
            if (HasSuspiciousContent(decoded))
                return new DecodedVariant("base64", trimmed, decoded);
        }
        catch { }
        return null;
    }

    private static DecodedVariant? TryDecodeRot13(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool changed = false;
        foreach (var c in text)
        {
            if (c is >= 'a' and <= 'z')
            {
                sb.Append((char)((c - 'a' + 13) % 26 + 'a'));
                changed = true;
            }
            else if (c is >= 'A' and <= 'Z')
            {
                sb.Append((char)((c - 'A' + 13) % 26 + 'A'));
                changed = true;
            }
            else sb.Append(c);
        }

        if (!changed) return null;
        var decoded = sb.ToString();
        return HasSuspiciousContent(decoded) ? new DecodedVariant("rot13", text, decoded) : null;
    }

    private static DecodedVariant? TryDecodeUrl(string text)
    {
        if (!text.Contains('%')) return null;
        try
        {
            var decoded = Uri.UnescapeDataString(text);
            return decoded != text && HasSuspiciousContent(decoded)
                ? new DecodedVariant("url", text, decoded) : null;
        }
        catch { return null; }
    }

    private static DecodedVariant? TryDecodeHex(string text)
    {
        var trimmed = text.Replace(" ", "").Replace("0x", "").Replace("\\x", "");
        if (trimmed.Length % 2 != 0 || trimmed.Length < 2) return null;
        if (!Regex.IsMatch(trimmed, @"^[0-9A-Fa-f]+$")) return null;

        try
        {
            var bytes = new byte[trimmed.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(trimmed.Substring(i * 2, 2), 16);
            var decoded = Encoding.UTF8.GetString(bytes);
            if (HasSuspiciousContent(decoded))
                return new DecodedVariant("hex", text, decoded);
        }
        catch { }
        return null;
    }

    private static DecodedVariant? TryDecodeLeetspeak(string text)
    {
        var lower = text.ToLowerInvariant();
        var sb = new StringBuilder();
        var i = 0;
        var changed = false;

        while (i < lower.Length)
        {
            var matched = false;
            foreach (var (leet, normal) in LeetMap)
            {
                if (i + leet.Length <= lower.Length && lower.Substring(i, leet.Length) == leet)
                {
                    sb.Append(normal);
                    i += leet.Length;
                    matched = true;
                    changed = true;
                    break;
                }
            }
            if (!matched)
            {
                sb.Append(lower[i]);
                i++;
            }
        }

        if (!changed) return null;
        var decoded = sb.ToString();
        return HasSuspiciousContent(decoded) ? new DecodedVariant("leetspeak", text, decoded) : null;
    }

    private static DecodedVariant? TryDecodeHomoglyphs(string text)
    {
        var sb = new StringBuilder(text.Length);
        var changed = false;

        foreach (var c in text)
        {
            if (HomoglyphMap.TryGetValue(c, out var replacement))
            {
                sb.Append(replacement);
                changed = true;
            }
            else sb.Append(c);
        }

        if (!changed) return null;
        var decoded = sb.ToString();
        return HasSuspiciousContent(decoded) ? new DecodedVariant("homoglyph", text, decoded) : null;
    }

    private static DecodedVariant? TryDecodeUnicode(string text)
    {
        if (!text.Contains("\\u")) return null;
        try
        {
            var decoded = Regex.Replace(text, @"\\u([0-9A-Fa-f]{4})",
                m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
            decoded = Regex.Replace(decoded, @"\\U([0-9A-Fa-f]{8})",
                m => char.ConvertFromUtf32(Convert.ToInt32(m.Groups[1].Value, 16)));

            if (decoded != text && HasSuspiciousContent(decoded))
                return new DecodedVariant("unicode", text, decoded);
        }
        catch { }
        return null;
    }

    private static bool HasSuspiciousContent(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lower = text.ToLowerInvariant();
        return lower.Contains("delete") || lower.Contains("drop") || lower.Contains("exec") ||
               lower.Contains("sudo") || lower.Contains("rm ") || lower.Contains("shutdown") ||
               lower.Contains("system") || lower.Contains("os.") || lower.Contains("subprocess") ||
               lower.Contains("__import__") || lower.Contains("eval") || lower.Contains("ignore") ||
               lower.Contains("bypass") || lower.Contains("jailbreak");
    }
}

public sealed record DecodedVariant(string Encoding, string Original, string Decoded);
