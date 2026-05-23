using LTAI.Core.Configuration;
using LTAI.Knowledge.Vector.Interfaces;
using Microsoft.Extensions.Logging;

namespace LTAI.Knowledge.Vector.Embedding;

/// Jina Embeddings v5 Omni — multimodal (text/image/audio) small embedding models
/// Models: jina-embeddings-v5-omni-small (768-dim), jina-embeddings-v5-omni-nano (512-dim)
/// Source: https://huggingface.co/jinaai/jina-embeddings-v5-omni

public sealed record JinaEmbeddingConfig
{
    public string ModelName { get; init; } = "jina-embeddings-v5-omni-small";
    public int Dimension { get; init; } = 768;
    public string HuggingFaceRepo { get; init; } = "jinaai/jina-embeddings-v5-omni";
    public string OnnxModelPath { get; init; } = "";                  // path to the ONNX model file
    public string OnnxTokenizerPath { get; init; } = "";               // path to tokenizer.json
    public int MaxSequenceLength { get; init; } = 8192;
    public bool EnableMultimodal { get; init; }                       // whether to use image/audio inputs
#if NET10_0_OR_GREATER
        = true;
#else
        = false;
#endif
    public int BatchSize { get; init; } = 32;
}

public enum JinaModelVariant { OmniSmall, OmniNano }

/// Preset configurations for Jina embedding models
public static class JinaModelPresets
{
    // jina-embeddings-v5-omni-small: 768-dim, ~500MB ONNX, supports text+image+audio
    public static JinaEmbeddingConfig OmniSmall => new()
    {
        ModelName = "jina-embeddings-v5-omni-small",
        Dimension = 768,
        HuggingFaceRepo = "jinaai/jina-embeddings-v5-omni",
        MaxSequenceLength = 8192,
        EnableMultimodal = true,
        BatchSize = 32
    };

    // jina-embeddings-v5-omni-nano: 512-dim, ~200MB ONNX, text-only with image embeddings
    public static JinaEmbeddingConfig OmniNano => new()
    {
        ModelName = "jina-embeddings-v5-omni-nano",
        Dimension = 512,
        HuggingFaceRepo = "jinaai/jina-embeddings-v5-omni",
        MaxSequenceLength = 8192,
        EnableMultimodal = true,
        BatchSize = 16
    };

    public static JinaEmbeddingConfig GetPreset(JinaModelVariant variant) => variant switch
    {
        JinaModelVariant.OmniNano => OmniNano,
        _ => OmniSmall
    };

    // Update LTAIOptions in a new appsettings.json to use Jina as the L0 embedding model.
    // Usage: JinaModelPresets.ApplyToL0(yourOptions, JinaModelVariant.OmniNano);
    // Then write the options back to JSON with JsonSerializer.
    public static Dictionary<string, object> GetL0Config(JinaModelVariant variant = JinaModelVariant.OmniSmall)
    {
        var preset = GetPreset(variant);
        return new Dictionary<string, object>
        {
            ["l0"] = new { provider = "jina", model = preset.ModelName },
            ["vector"] = new { dimension = preset.Dimension, backend = "jina-onnx", cache_size_mb = preset.Dimension == 768 ? 512 : 256 }
        };
    }
}

/// Backend that wraps OnnxEmbeddingBackend with Jina-specific preprocessing.
/// Jina v5 Omni uses task-specific prefixes: "text: ", "image: ", "audio: "
/// which must be prepended to inputs before embedding.
[Obsolete("Cloud embedding APIs are being phased out. Use ONNX local embedding (AddLTAIVectorLocal) instead. Jina backend retained for backward compatibility only.")]
public sealed class JinaEmbeddingBackend : IEmbeddingBackend, IDisposable
{
    private readonly OnnxEmbeddingBackend _onnxBackend;
    private readonly JinaEmbeddingConfig _config;
    private readonly ILogger<JinaEmbeddingBackend> _logger;

    public int Dimension => _config.Dimension;
    public string ModelName => _config.ModelName;

    public JinaEmbeddingBackend(
        JinaEmbeddingConfig config,
        ILogger<JinaEmbeddingBackend>? logger = null)
    {
        _config = config;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<JinaEmbeddingBackend>.Instance;

        var onnxConfig = new OnnxEmbeddingConfig
        {
            ModelPath = config.OnnxModelPath,
            TokenizerPath = config.OnnxTokenizerPath,
            Dimension = config.Dimension,
            ModelName = config.ModelName
        };

        _onnxBackend = new OnnxEmbeddingBackend(onnxConfig, _logger as ILogger<OnnxEmbeddingBackend>);
    }

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var prefixed = texts.Select(t => $"text: {t}").ToArray();
        return await _onnxBackend.EmbedAsync(prefixed, ct);
    }

    public async Task<float[]> EmbedSingleAsync(string text, CancellationToken ct = default)
    {
        var prefixedInput = $"text: {text}";
        var results = await _onnxBackend.EmbedAsync(new[] { prefixedInput }, ct);
        return results[0];
    }

    // Multimodal: embed an image description with task prefix
    public async Task<float[]> EmbedImageDescriptionAsync(string imageDescription, CancellationToken ct = default)
    {
        var prefixedInput = $"image: {imageDescription}";
        var results = await _onnxBackend.EmbedAsync(new[] { prefixedInput }, ct);
        return results[0];
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _onnxBackend.InitializeAsync();
        _logger.LogInformation("JinaEmbeddingBackend initialized: model={Model} dim={Dim}", _config.ModelName, _config.Dimension);
    }

    public void Dispose() => _onnxBackend.Dispose();
}

/// Helper to download Jina ONNX model files from HuggingFace
public static class JinaModelDownloader
{
    private static readonly HttpClient _http = new();

    public static async Task<JinaEmbeddingConfig> DownloadModelAsync(
        string cacheDir,
        JinaModelVariant variant = JinaModelVariant.OmniSmall,
        CancellationToken ct = default)
    {
        var preset = JinaModelPresets.GetPreset(variant);
        var modelDir = Path.Combine(cacheDir, "jina", preset.ModelName);
        Directory.CreateDirectory(modelDir);

        var onnxPath = Path.Combine(modelDir, "model.onnx");
        var tokenizerPath = Path.Combine(modelDir, "tokenizer.json");

        if (!File.Exists(onnxPath))
        {
            var variantPath = variant == JinaModelVariant.OmniNano ? "onnx_nano" : "onnx_small";
            var onnxUrl = $"https://huggingface.co/{preset.HuggingFaceRepo}/resolve/main/{variantPath}/model.onnx";
            await DownloadFileAsync(onnxUrl, onnxPath, ct);
        }

        if (!File.Exists(tokenizerPath))
        {
            var tokenizerUrl = $"https://huggingface.co/{preset.HuggingFaceRepo}/resolve/main/tokenizer.json";
            await DownloadFileAsync(tokenizerUrl, tokenizerPath, ct);
        }

        return preset with { OnnxModelPath = onnxPath, OnnxTokenizerPath = tokenizerPath };
    }

    private static async Task DownloadFileAsync(string url, string path, CancellationToken ct)
    {
        var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(path);
        await stream.CopyToAsync(file, ct);
    }
}
