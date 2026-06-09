namespace LTAI.Mm.Core;

internal static class FloatCodec
{
    internal static (bool Negative, sbyte Exponent, ulong Mantissa) ParseDecimalString(string s)
    {
        s = s.Trim();
        bool negative = s.StartsWith('-');
        if (negative) s = s[1..];

        int dot = s.IndexOf('.');
        int exp = s.IndexOfAny(['e', 'E']);

        if (exp >= 0)
        {
            string basePart = s[..exp];
            int expVal = int.Parse(s[(exp + 1)..]);

            if (dot >= 0)
            {
                string intPart = basePart[..dot];
                string fracPart = basePart[(dot + 1)..];
                string combined = intPart + fracPart;
                expVal -= fracPart.Length;
                return (negative, (sbyte)expVal, ulong.Parse(combined.TrimStart('0').PadLeft(1, '0')));
            }
            else
            {
                return (negative, (sbyte)expVal, ulong.Parse(basePart));
            }
        }

        if (dot >= 0)
        {
            string intPart = s[..dot];
            string fracPart = s[(dot + 1)..];
            string combined = intPart + fracPart;
            int expVal = -fracPart.Length;
            string trimmed = combined.TrimStart('0');
            if (trimmed.Length == 0) trimmed = "0";
            return (negative, (sbyte)expVal, ulong.Parse(trimmed));
        }

        return (negative, 0, ulong.Parse(s));
    }

    internal static string FormatDecimal(bool negative, sbyte exponent, ulong mantissa)
    {
        string mantStr = mantissa.ToString();
        if (exponent == 0) return (negative ? "-" : "") + mantStr;

        if (exponent > 0)
        {
            mantStr = mantStr.PadRight(mantStr.Length + exponent, '0');
            return (negative ? "-" : "") + mantStr;
        }
        else
        {
            int dotPos = mantStr.Length + exponent;
            if (dotPos <= 0)
            {
                mantStr = new string('0', -dotPos + 1) + mantStr;
                dotPos = 1;
            }
            string result = mantStr[..dotPos] + "." + mantStr[dotPos..];
            return (negative ? "-" : "") + result;
        }
    }
}
