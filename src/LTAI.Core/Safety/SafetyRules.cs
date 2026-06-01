using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace LTAI.Core.Safety;

public static class SafetyRules
{
    private static readonly SearchValues<string> PemMarkers = SearchValues.Create(["-----BEGIN", "PRIVATE KEY", "RSA"], StringComparison.Ordinal);
    private static readonly Regex ApiKeyRx = new(
        @"(?:sk-|pk-|api[_-]?key|secret|token|password)[\s\-_:：]+[a-zA-Z0-9]{16,}", RegexOptions.IgnoreCase);
    private static readonly Regex CreditCardRx = new(@"\b\d{4}[\s\-]?\d{4}[\s\-]?\d{4}[\s\-]?\d{4}\b");
    private static readonly Regex SqlInjectRx = new(
        @"\b(DROP\s+TABLE|TRUNCATE\s+TABLE|DELETE\s+FROM\s+|EXEC\s*\(|xp_cmdshell)\b", RegexOptions.IgnoreCase);
    private static readonly Regex XssRx = new(
        @"<script[^>]*>.*?</script>|javascript:\s*\(|onerror\s*=|onload\s*=|eval\s*\(", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex PhoneRx = new(@"\+?\d{1,3}[\s\-]?\d{3,4}[\s\-]?\d{4,}");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSafeByRules(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        if (ApiKeyRx.IsMatch(text)) return false;
        if (CreditCardRx.IsMatch(text)) return false;
        if (PhoneRx.IsMatch(text)) return false;
        if (text.AsSpan().IndexOfAny(PemMarkers) >= 0) return false;
        if (SqlInjectRx.IsMatch(text)) return false;
        if (XssRx.IsMatch(text)) return false;
        return true;
    }
}
