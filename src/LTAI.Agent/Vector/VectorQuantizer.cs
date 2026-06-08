using TurboQuant;
using TurboQuant.Core.Packing;
using TurboQuant.Core.Quantizers;

namespace LTAI.Agent.Vector;

public static class VectorQuantizer
{
    public const int Dim = 384;
    public const int Bits = 4;

    private static readonly TurboQuantMSE Instance = TurboQuantBuilder
        .Create(Dim)
        .WithBits(Bits)
        .BuildMSE();

    public static int PackedByteCount => Dim * Bits / 8;

    public static byte[] QuantizeToBytes(float[] vector)
    {
        var packed = Instance.Quantize(vector);
        return packed.ToBytes();
    }

    public static float[] DequantizeFromBytes(byte[] bytes)
    {
        var packed = PackedVector.FromBytes(bytes);
        return Instance.Dequantize(packed);
    }

    public static float ApproxSimilarity(byte[] a, byte[] b)
    {
        var pa = PackedVector.FromBytes(a);
        var pb = PackedVector.FromBytes(b);
        return Instance.ApproxSimilarity(pa, pb);
    }

    public static float ApproxSimilarity(PackedVector a, PackedVector b)
        => Instance.ApproxSimilarity(a, b);

    public static PackedVector Quantize(float[] vector)
        => Instance.Quantize(vector);

    public static float[] Dequantize(PackedVector packed)
        => Instance.Dequantize(packed);

    public static float CosineDistance(PackedVector a, PackedVector b)
        => 1f - Instance.ApproxSimilarity(a, b);
}
