using System;
using System.Threading;
using System.Threading.Tasks;

namespace LTAI.AI.Governors;

/// <summary>
/// L1 本地推理引擎统一接口。支持 GGUF (llama.cpp) 和 ONNX 两种后端。
/// 扩展支持潜空间递归 (RecursiveMAS)
/// </summary>
public interface IL1InferenceEngine : IDisposable
{
    bool IsReady { get; }
    string ModelName { get; }
    string EngineType { get; } // "gguf" or "onnx"
    long ModelSizeMB { get; }
    int HiddenDimension { get; } // 模型隐藏维度 (用于 RecursiveLink 维度对齐)

    Task InitializeAsync(string modelPath, CancellationToken ct = default);
    Task<string> GenerateAsync(string prompt, float temperature = 0.7f, int maxTokens = 256, CancellationToken ct = default);

    /// <summary>
    /// 将文本编码为潜空间状态 (最后一层隐藏状态)
    /// 用于 RecursiveMAS 的潜空间传递
    /// </summary>
    Task<LatentState> EncodeToLatentAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// 在潜空间中 refine 状态 (不生成文本)
    /// 用于递归循环中的中间轮次
    /// </summary>
    Task<LatentState> RefineLatentAsync(LatentState latent, float temperature = 0.6f, CancellationToken ct = default);

    /// <summary>
    /// 将潜空间状态解码为文本
    /// 仅用于递归循环的最终轮
    /// </summary>
    Task<string> DecodeFromLatentAsync(LatentState latent, CancellationToken ct = default);
}
