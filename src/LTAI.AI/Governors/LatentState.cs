using System;

namespace LTAI.AI.Governors;

/// <summary>
/// 潜空间状态载体 (Latent State)
/// 用于在 L1/L2 之间传递隐藏状态，避免重复文本解码
/// 灵感来自 RecursiveMAS 的 Latent-Space Recursion
/// </summary>
public sealed record LatentState
{
    /// <summary>
    /// 最后一层隐藏状态 (hidden states)
    /// </summary>
    public float[] Embedding { get; init; } = Array.Empty<float>();

    /// <summary>
    /// 注意力掩码 (可选)
    /// </summary>
    public float[]? AttentionMask { get; init; }

    /// <summary>
    /// 键值缓存 (用于增量推理)
    /// </summary>
    public float[]? KvCache { get; init; }

    /// <summary>
    /// 当前递归深度
    /// </summary>
    public int RecursionDepth { get; init; }

    /// <summary>
    /// 来源代理标识
    /// </summary>
    public string SourceAgent { get; init; } = "";

    /// <summary>
    /// 是否已解码为文本 (仅最后一轮为 true)
    /// </summary>
    public bool IsDecoded { get; init; }

    /// <summary>
    /// 对应的文本 (仅当 IsDecoded=true 时有效)
    /// </summary>
    public string? DecodedText { get; init; }

    /// <summary>
    /// 语义相似度 (与目标分布的余弦相似度，用于训练信号)
    /// </summary>
    public float SemanticSimilarity { get; init; } = 1.0f;

    /// <summary>
    /// 创建未解码的潜状态
    /// </summary>
    public static LatentState Create(float[] embedding, int depth = 0, string source = "unknown")
    {
        return new LatentState
        {
            Embedding = embedding,
            RecursionDepth = depth,
            SourceAgent = source,
            IsDecoded = false
        };
    }

    /// <summary>
    /// 创建已解码的潜状态 (最终输出)
    /// </summary>
    public static LatentState CreateDecoded(float[] embedding, string text, int depth)
    {
        return new LatentState
        {
            Embedding = embedding,
            DecodedText = text,
            RecursionDepth = depth,
            IsDecoded = true
        };
    }
}
