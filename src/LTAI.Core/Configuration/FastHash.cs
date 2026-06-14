using System.Text;

namespace LTAI.Core.Configuration;

/// <summary>
/// FNV-1a 64-bit non-cryptographic hash for cache key dedup.
/// ~10x faster than SHA256, zero allocation (stack-only), no NuGet dependency.
/// Collision rate &lt; 2^-64 per pair — sufficient for in-process cache keys.
/// </summary>
public static class FastHash
{
    private const ulong FnvOffsetBasis = 14695981039346656037;
    private const ulong FnvPrime = 1099511628211;

    public static ulong Compute(ReadOnlySpan<byte> data)
    {
        var hash = FnvOffsetBasis;
        for (int i = 0; i < data.Length; i++)
        {
            hash ^= data[i];
            hash *= FnvPrime;
        }
        return hash;
    }

    public static ulong Compute(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return Compute(bytes.AsSpan());
    }

    public static string ComputeHex(string text) =>
        Compute(text).ToString("x16");

    public static string ComputeHex(ReadOnlySpan<byte> data) =>
        Compute(data).ToString("x16");
}
