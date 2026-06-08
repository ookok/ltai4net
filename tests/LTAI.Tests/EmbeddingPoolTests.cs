using System.Reflection;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit;

namespace LTAI.Tests;

public class EmbeddingPoolTests
{
    private static readonly Type s_poolType = LoadPoolType();

    private static Type LoadPoolType()
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "LTAI.AI")
            ?? Assembly.Load("LTAI.AI");
        return assembly.GetType("LTAI.AI.EmbeddingPool")!;
    }

    [Fact]
    public void GetOrAddPool_ReturnsSamePool_ForSameDim()
    {
        var vec = new float[] { 1, 2, 3, 4 };
        var method = s_poolType.GetMethod("L2Normalize",
            BindingFlags.Public | BindingFlags.Static,
            [typeof(float[])]);
        Assert.NotNull(method);

        var r1 = (float[])method.Invoke(null, [vec])!;
        var r2 = (float[])method.Invoke(null, [vec])!;

        Assert.Equal(r1.Length, r2.Length);
        Assert.Equal(r1, r2);
    }

    [Fact]
    public void GetOrAddPool_CreateNew_ForDifferentDim()
    {
        var method = s_poolType.GetMethod("L2Normalize",
            BindingFlags.Public | BindingFlags.Static,
            [typeof(float[])]);
        Assert.NotNull(method);

        var v4 = new float[] { 1, 2, 3, 4 };
        var v8 = new float[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        var r4 = (float[])method.Invoke(null, [v4])!;
        var r8 = (float[])method.Invoke(null, [v8])!;

        Assert.Equal(4, r4.Length);
        Assert.Equal(8, r8.Length);
    }

    [Fact]
    public void MeanPool_ReturnsCorrectDimension()
    {
        var method = s_poolType.GetMethod("MeanPool",
            BindingFlags.Public | BindingFlags.Static,
            [typeof(Tensor<float>), typeof(Tensor<long>), typeof(int)]);
        if (method == null) return;

        var embedding = new DenseTensor<float>(new float[2 * 3 * 4], [2, 3, 4]);
        var mask = new DenseTensor<long>(new long[] { 1, 1, 0, 1, 1, 0 }, [2, 3]);

        var result = (float[])method.Invoke(null, [embedding, mask, 4])!;

        Assert.Equal(4, result.Length);
    }

    [Fact]
    public void L2Normalize_ProducesUnitVector()
    {
        var vec = new float[] { 3, 4 };
        var method = s_poolType.GetMethod("L2Normalize",
            BindingFlags.Public | BindingFlags.Static,
            [typeof(float[])]);
        Assert.NotNull(method);

        var result = (float[])method.Invoke(null, [vec])!;

        var norm = MathF.Sqrt(result[0] * result[0] + result[1] * result[1]);
        Assert.Equal(1.0f, norm, 5);
    }
}
