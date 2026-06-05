using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics;
using Microsoft.Extensions.Logging;

namespace LTAI.AI;

public static class VectorMath
{
    /// <summary>
    /// SIMD-accelerated cosine similarity between two vectors.
    /// Returns dot(a,b) / (|a| * |b|), or 0 if either norm is zero.
    /// Handles mismatched lengths (uses Math.Min) with a debug warning.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int len = a.Length <= b.Length ? a.Length : b.Length;
        if (len == 0) return 0;

        float dot = 0, normA = 0, normB = 0;
        int i = 0;

        if (Vector.IsHardwareAccelerated && len >= Vector<float>.Count)
        {
            int vecLen = Vector<float>.Count;
            var aVecs = MemoryMarshal.Cast<float, Vector<float>>(a);
            var bVecs = MemoryMarshal.Cast<float, Vector<float>>(b);
            var vdot = Vector<float>.Zero;
            var vna = Vector<float>.Zero;
            var vnb = Vector<float>.Zero;
            int vecCount = len / vecLen;
            for (int j = 0; j < vecCount; j++)
            {
                vdot += aVecs[j] * bVecs[j];
                vna += aVecs[j] * aVecs[j];
                vnb += bVecs[j] * bVecs[j];
            }
            for (int k = 0; k < vecLen; k++)
            {
                dot += vdot[k];
                normA += vna[k];
                normB += vnb[k];
            }
            i = vecCount * vecLen;
        }

        for (; i < len; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom == 0 ? 0 : dot / denom;
    }

    /// <summary>
    /// Cosine distance = 1 - cosineSimilarity. Used by HNSW (lower = closer).
    /// Returns 1 when either norm is zero.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float CosineDistance(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int len = a.Length <= b.Length ? a.Length : b.Length;
        if (len == 0) return 1;

        float dot = 0, normA = 0, normB = 0;
        int i = 0;

        if (Vector.IsHardwareAccelerated && len >= Vector<float>.Count)
        {
            int vecLen = Vector<float>.Count;
            var aVecs = MemoryMarshal.Cast<float, Vector<float>>(a);
            var bVecs = MemoryMarshal.Cast<float, Vector<float>>(b);
            var vdot = Vector<float>.Zero;
            var vna = Vector<float>.Zero;
            var vnb = Vector<float>.Zero;
            int vecCount = len / vecLen;
            for (int j = 0; j < vecCount; j++)
            {
                vdot += aVecs[j] * bVecs[j];
                vna += aVecs[j] * aVecs[j];
                vnb += bVecs[j] * bVecs[j];
            }
            for (int k = 0; k < vecLen; k++)
            {
                dot += vdot[k];
                normA += vna[k];
                normB += vnb[k];
            }
            i = vecCount * vecLen;
        }

        for (; i < len; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom == 0 ? 1 : 1 - dot / denom;
    }
}
