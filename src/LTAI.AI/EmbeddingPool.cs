using Microsoft.ML.OnnxRuntime.Tensors;

namespace LTAI.AI;

internal static class EmbeddingPool
{
    public static float[] MeanPool(Tensor<float> embedding, Tensor<long> attentionMask, int defaultDimension)
    {
        int batchSize = embedding.Dimensions[0];
        int seqLen = embedding.Dimensions[1];
        int hiddenDim = embedding.Dimensions[2];
        if (hiddenDim != defaultDimension)
        {
#if DEBUG
            System.Diagnostics.Debug.WriteLine(
                $"[LocalEmbedder] WARNING: model outputs {hiddenDim}-dim but target is {defaultDimension}; " +
                $"{(hiddenDim > defaultDimension ? "truncating" : "padding")} to {defaultDimension}.");
#endif
        }
        var result = new float[defaultDimension];
        float[] sum = new float[hiddenDim];
        int count = 0;
        for (int j = 0; j < seqLen; j++)
        {
            if (attentionMask[0, j] == 0) continue;
            count++;
            for (int k = 0; k < hiddenDim; k++)
                sum[k] += embedding[0, j, k];
        }
        if (count > 0)
        {
            for (int k = 0; k < hiddenDim; k++)
                sum[k] /= count;
        }
        Array.Copy(sum, result, Math.Min(hiddenDim, defaultDimension));
        return result;
    }

    /// <summary>L2-normalize a vector. MODIFIES the input array in-place and returns it.</summary>
    public static float[] L2Normalize(float[] vec)
    {
        float norm = 0;
        foreach (var v in vec) norm += v * v;
        norm = MathF.Sqrt(norm);
        if (norm < 1e-12f) return vec;
        for (int i = 0; i < vec.Length; i++)
            vec[i] /= norm;
        return vec;
    }

    /// <summary>
    /// L2-normalize the first <paramref name="len"/> elements of <paramref name="buf"/> in-place,
    /// and return a new array containing those normalized elements. The original buffer is mutated.
    /// </summary>
    public static float[] L2NormalizeSubarray(float[] buf, int len)
    {
        float norm = 0;
        for (int i = 0; i < len; i++) norm += buf[i] * buf[i];
        norm = MathF.Sqrt(norm);
        if (norm < 1e-12f)
        {
            var fallback = new float[len];
            fallback.AsSpan().Clear();
            return fallback;
        }
        for (int i = 0; i < len; i++) buf[i] /= norm;
        var result = new float[len];
        Array.Copy(buf, result, len);
        return result;
    }
}
