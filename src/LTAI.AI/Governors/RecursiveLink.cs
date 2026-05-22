using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace LTAI.AI.Governors;

/// <summary>
/// RecursiveLink 适配器
/// 用于在不同模型的潜空间之间进行轻量级映射
/// 灵感来自 RecursiveMAS 论文: R_out(h) = W3*h + W2*σ(W1*h)
/// 包含残差连接以保持原始语义，仅学习分布偏移
/// </summary>
public sealed class RecursiveLink
{
    private readonly int _sourceDim;
    private readonly int _targetDim;
    
    // 2 层残差投影矩阵 (扁平化存储)
    private readonly float[] _W1;  // [hiddenDim, sourceDim]
    private readonly float[] _W2;  // [targetDim, hiddenDim]
    private readonly float[] _W3;  // [targetDim, sourceDim] - 维度对齐层

    private readonly int _hiddenDim;

    /// <summary>
    /// 创建 RecursiveLink
    /// </summary>
    /// <param name="sourceDim">源模型隐藏维度</param>
    /// <param name="targetDim">目标模型隐藏维度</param>
    /// <param name="hiddenDim">适配器内部隐藏维度 (通常 = min(sourceDim, targetDim))</param>
    public RecursiveLink(int sourceDim, int targetDim, int? hiddenDim = null)
    {
        _sourceDim = sourceDim;
        _targetDim = targetDim;
        _hiddenDim = hiddenDim ?? Math.Min(sourceDim, targetDim);

        _W1 = new float[_hiddenDim * sourceDim];
        _W2 = new float[targetDim * _hiddenDim];
        _W3 = new float[targetDim * sourceDim];

        // 初始化: W3 初始化为单位矩阵投影 (如果维度相同) 或零矩阵
        // W1, W2 初始化为小随机值
        InitializeWeights();
    }

    private void InitializeWeights()
    {
        var rng = new Random(42);
        var scale1 = 1.0f / MathF.Sqrt(_sourceDim);
        var scale2 = 1.0f / MathF.Sqrt(_hiddenDim);

        for (int i = 0; i < _W1.Length; i++) _W1[i] = (float)(rng.NextDouble() * 2 - 1) * scale1;
        for (int i = 0; i < _W2.Length; i++) _W2[i] = (float)(rng.NextDouble() * 2 - 1) * scale2;

        // W3: 如果维度相同，初始化为单位矩阵；否则为零
        if (_sourceDim == _targetDim)
        {
            for (int i = 0; i < _sourceDim; i++) _W3[i * _sourceDim + i] = 1.0f;
        }
    }

    /// <summary>
    /// 内环链接 (Inner Link): 同一模型内的潜空间自映射
    /// R_in(h) = h + W2*σ(W1*h)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LatentState InnerLink(LatentState state)
    {
        var hidden = MatMul(_W1, _hiddenDim, _sourceDim, state.Embedding);
        ApplyGeluInPlace(hidden);
        var residual = MatMul(_W2, _targetDim, _hiddenDim, hidden);

        var output = new float[_targetDim];
        for (int i = 0; i < _targetDim; i++)
            output[i] = state.Embedding[i % _sourceDim] + residual[i];

        return state with { Embedding = output };
    }

    /// <summary>
    /// 外环链接 (Outer Link): 跨模型潜空间映射
    /// R_out(h) = W3*h + W2*σ(W1*h)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LatentState OuterLink(LatentState state)
    {
        var aligned = MatMul(_W3, _targetDim, _sourceDim, state.Embedding);
        
        var hidden = MatMul(_W1, _hiddenDim, _sourceDim, state.Embedding);
        ApplyGeluInPlace(hidden);
        var nonlinear = MatMul(_W2, _targetDim, _hiddenDim, hidden);

        var output = new float[_targetDim];
        for (int i = 0; i < _targetDim; i++)
            output[i] = aligned[i] + nonlinear[i];

        return state with { Embedding = output, SourceAgent = "transferred" };
    }

    /// <summary>
    /// 内环+外环联合传递 (完整 RecursiveLink 流程)
    /// </summary>
    public LatentState Transfer(LatentState state)
    {
        var inner = InnerLink(state);
        return OuterLink(inner);
    }

    /// <summary>
    /// 使用训练信号更新权重 (简化版余弦相似度损失梯度下降)
    /// Loss = 1 - cos(output, target)
    /// </summary>
    public void UpdateWeights(LatentState input, float[] targetEmbedding, float learningRate = 1e-4f)
    {
        var output = Transfer(input);
        var loss = 1.0f - CosineSimilarity(output.Embedding, targetEmbedding);

        // 简化梯度更新 (实际应使用反向传播)
        // 这里仅作结构预留
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float[] MatMul(float[] weights, int rows, int cols, float[] input)
    {
        var output = new float[rows];
        for (int i = 0; i < rows; i++)
        {
            float sum = 0;
            for (int j = 0; j < cols; j++)
                sum += weights[i * cols + j] * input[j];
            output[i] = sum;
        }
        return output;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyGeluInPlace(float[] x)
    {
        for (int i = 0; i < x.Length; i++)
        {
            var c = 0.044715f;
            var cube = x[i] * x[i] * x[i];
            x[i] = 0.5f * x[i] * (1.0f + MathF.Tanh(0.7978845608f * (x[i] + c * cube)));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB) + 1e-8f);
    }

    public int SourceDim => _sourceDim;
    public int TargetDim => _targetDim;
}
