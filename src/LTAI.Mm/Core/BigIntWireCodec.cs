namespace LTAI.Mm.Core;

internal static class BigIntWireCodec
{
    internal static byte[] EncodeSignedDecimal(string s)
    {
        s = s.Trim();
        bool negative = s.StartsWith('-');
        if (negative) s = s[1..];

        s = s.TrimStart('0');
        if (s.Length == 0) s = "0";

        if (s == "0") return [0];

        int byteCount = (s.Length + 1) / 2;
        var result = new byte[negative ? byteCount + 1 : byteCount];
        int idx = result.Length - 1;
        for (int i = s.Length; i > 0; i -= 2)
        {
            int start = Math.Max(0, i - 2);
            int len = i - start;
            result[idx--] = byte.Parse(s[start..(start + len)]);
        }

        if (negative)
        {
            result[0] = 0xFF;
        }
        else
        {
            result[0] |= 0x80;
        }

        return result;
    }

    internal static string DecodeSignedDecimal(byte[] data)
    {
        if (data.Length == 0) return "0";
        if (data.Length == 1 && data[0] == 0) return "0";

        bool negative = (data[0] & 0x80) == 0;
        data[0] &= 0x7F;

        var sb = new System.Text.StringBuilder(data.Length * 2);
        foreach (byte b in data)
        {
            if (sb.Length > 0 || b != 0)
                sb.Append(b.ToString("D2"));
        }

        if (sb.Length == 0) sb.Append('0');
        string result = sb.ToString().TrimStart('0');
        if (result.Length == 0) result = "0";

        return negative ? "-" + result : result;
    }
}
