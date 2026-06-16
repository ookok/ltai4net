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

    private static long _totalQuantized;
    private static double _accumulatedError;
    private static double _maxError;

    public static int PackedByteCount => Dim * Bits / 8;

    /// <summary>Average reconstruction error since last reset.</summary>
    public static double AverageError => _totalQuantized > 0 ? _accumulatedError / _totalQuantized : 0;

    /// <summary>Peak reconstruction error since last reset.</summary>
    public static double MaxError => _maxError;

    /// <summary>Total vectors quantized since last reset.</summary>
    public static long TotalQuantized => Interlocked.Read(ref _totalQuantized);

    /// <summary>Reset precision monitoring counters.</summary>
    public static void ResetMetrics()
    {
        Interlocked.Exchange(ref _totalQuantized, 0);
        _accumulatedError = 0;
        _maxError = 0;
    }

    /// <summary>Get a formatted metrics report.</summary>
    public static string GetMetricsReport()
    {
        var avg = AverageError;
        var max = MaxError;
        return $"Quantization precision: avg_error={avg:F6}, max_error={max:F6}, vectors={TotalQuantized}, dim={Dim}, bits={Bits}";
    }

    public static byte[] QuantizeToBytes(float[] vector)
    {
        if (vector.Length != Dim)
            throw new ArgumentException($"VectorQuantizer: expected {Dim} dimensions, got {vector.Length}");
        var packed = Instance.Quantize(vector);
        TrackPrecision(vector, packed);
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
    {
        if (vector.Length != Dim)
            throw new ArgumentException($"VectorQuantizer: expected {Dim} dimensions, got {vector.Length}");
        var packed = Instance.Quantize(vector);
        TrackPrecision(vector, packed);
        return packed;
    }

    public static float[] Dequantize(PackedVector packed)
        => Instance.Dequantize(packed);

    public static float CosineDistance(PackedVector a, PackedVector b)
        => 1f - Instance.ApproxSimilarity(a, b);

    private static void TrackPrecision(float[] original, PackedVector packed)
    {
        var reconstructed = Instance.Dequantize(packed);
        var len = Math.Min(original.Length, reconstructed.Length);
        double error = 0;
        for (int i = 0; i < len; i++)
        {
            var diff = original[i] - reconstructed[i];
            error += diff * diff;
        }
        error = Math.Sqrt(error / len);
        Interlocked.Increment(ref _totalQuantized);
        lock (Instance) // thread-safe update for accumulator fields
        {
            _accumulatedError += error;
            if (error > _maxError) _maxError = error;
        }
    }
}
