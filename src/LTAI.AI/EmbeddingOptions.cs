// Copyright (c) LTAI. All rights reserved.

namespace LTAI.AI;

/// <summary>
/// P13.1 + P13.2: configuration for <see cref="LocalEmbedder"/> model loading
/// and execution provider selection. Read at <see cref="LocalEmbedder"/>
/// construction time; changing these values after the model is loaded
/// requires restarting the process (or re-instantiating the embedder).
/// </summary>
public sealed class EmbeddingOptions
{
    /// <summary>
    /// Preferred GPU execution provider. <c>auto</c> probes DirectML
    /// (Windows, no-NVIDIA required) → CUDA (NVIDIA) → CPU in order.
    /// <c>dml</c> forces DirectML; throws if unavailable. <c>cuda</c> forces
    /// CUDA; throws if unavailable. <c>cpu</c> skips GPU probes entirely.
    /// Default: <c>auto</c>.
    /// </summary>
    public string Gpu { get; set; } = "auto";

    /// <summary>
    /// Model quantization preference. <c>auto</c> prefers the
    /// <c>model.int8.onnx</c> variant if downloaded and the upstream
    /// HuggingFace model has a quantized export (MiniLM does, BGE doesn't).
    /// <c>int8</c> requires a quantized file; falls back to FP32 with a
    /// warning if not present. <c>fp32</c> always uses the original
    /// <c>model.onnx</c>. Default: <c>auto</c>.
    /// </summary>
    public string Quantization { get; set; } = "auto";

    /// <summary>
    /// GPU device ID (for multi-GPU systems). Default: 0.
    /// </summary>
    public int DeviceId { get; set; } = 0;

    /// <summary>
    /// When true, lets ONNX Runtime pick a graph optimization level
    /// appropriate for the chosen EP (default true; recommended).
    /// </summary>
    public bool EnableGraphOptimization { get; set; } = true;

    /// <summary>
    /// P14.9: per-model quantization overrides (mirrors
    /// <c>LTAI.Core.Configuration.EmbeddingConfig.Models</c>). Keyed by
    /// model id (e.g. <c>minilm-l6-v2</c>); value is <c>int8</c> / <c>fp32</c>
    /// / <c>auto</c>. <b>Priority: per-model &gt; <see cref="Quantization"/>.</b>
    /// Populated by <c>MultiProviderChatClient.AddLTAIAI</c> from
    /// <c>LTAIOptions.Embedding.Models</c> at DI resolution time so the
    /// static <see cref="LocalEmbedder.Options"/> mirrors config.
    /// </summary>
    public IDictionary<string, string> Models { get; set; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// P14.9: effective quantization preference for a model id — per-model
    /// override first, then global <see cref="Quantization"/>, then <c>auto</c>.
    /// </summary>
    public string GetQuantizationFor(string modelId)
    {
        if (!string.IsNullOrEmpty(modelId)
            && Models.TryGetValue(modelId, out var m)
            && !string.IsNullOrWhiteSpace(m))
        {
            return m.Trim().ToLowerInvariant();
        }
        return string.IsNullOrWhiteSpace(Quantization) ? "auto" : Quantization.Trim().ToLowerInvariant();
    }
}
