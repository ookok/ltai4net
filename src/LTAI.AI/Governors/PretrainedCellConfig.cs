using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed class PretrainedCellConfig
{
    public Dictionary<string, OnnxModelConfig> Models { get; init; } = new();
    public string ModelsDirectory { get; init; } = "synaptic/pretrained";
    public bool AutoDownload { get; init; } = true;
    public bool FallbackToSelfTrained { get; init; } = true;
    public float SelfTrainedOverrideThreshold { get; init; } = 0.75f;
}

public static class PretrainedModelRegistry
{
    public static Dictionary<string, OnnxModelConfig> GetDefaultModels(string modelsDirectory)
    {
        return new Dictionary<string, OnnxModelConfig>
        {
            ["code"] = new OnnxModelConfig
            {
                Domain = "code",
                ModelPath = Path.Combine(modelsDirectory, "code", "model.onnx"),
                Labels = new[] { "locate_symbol", "find_references", "trace_dependencies", "semantic_search", "browse_structure", "cross_layer_trace", "ambiguous" },
                MaxSequenceLength = 128,
                MinConfidence = 0.5f,
                Source = "context4ai/intent-router-onnx",
                Description = "Code intent classification (EN/ZH)"
            },
            ["greeting"] = new OnnxModelConfig
            {
                Domain = "greeting",
                ModelPath = Path.Combine(modelsDirectory, "greeting", "model.onnx"),
                Labels = new[] { "greeting", "farewell", "thank_you", "affirmation", "negation", "small_talk", "bot_capabilities", "feedback_positive", "feedback_negative", "clarification", "suggestion", "language_change" },
                MaxSequenceLength = 64,
                MinConfidence = 0.6f,
                Source = "tanaos/tanaos-intent-classifier-v1",
                Description = "Chatbot intent classification"
            },
            ["general"] = new OnnxModelConfig
            {
                Domain = "general",
                ModelPath = Path.Combine(modelsDirectory, "general", "model.onnx"),
                Labels = new[] { "arithmetic", "symbolic_reasoning", "factual_lookup", "creative_synthesis", "code_generation", "security_risk" },
                MaxSequenceLength = 128,
                MinConfidence = 0.5f,
                Source = "RunsOnBacon/distilbert-intent-classifier-onnx-int8",
                Description = "General intent classification (INT8 quantized)"
            }
        };
    }

    public static async Task<bool> DownloadModelAsync(OnnxModelConfig config, string targetDir, ILogger logger, CancellationToken ct = default)
    {
        var modelDir = Path.GetDirectoryName(config.ModelPath) ?? targetDir;
        Directory.CreateDirectory(modelDir);

        if (File.Exists(config.ModelPath))
        {
            logger.LogInformation("Model already exists: {Domain}", config.Domain);
            return true;
        }

        logger.LogInformation("Downloading model: {Domain} from {Source}", config.Domain, config.Source);

        try
        {
            var downloadUrls = GetDownloadUrls(config.Source);
            if (downloadUrls == null)
            {
                logger.LogWarning("No download URL available for: {Source}", config.Source);
                return false;
            }

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(10);

            foreach (var (fileName, url) in downloadUrls)
            {
                var filePath = Path.Combine(modelDir, fileName);
                if (File.Exists(filePath)) continue;

                logger.LogInformation("Downloading: {FileName}", fileName);
                var data = await client.GetByteArrayAsync(url, ct);
                await File.WriteAllBytesAsync(filePath, data, ct);
            }

            logger.LogInformation("Model downloaded: {Domain}", config.Domain);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to download model: {Domain}", config.Domain);
            return false;
        }
    }

    private static Dictionary<string, string>? GetDownloadUrls(string source)
    {
        return source switch
        {
            "context4ai/intent-router-onnx" => new Dictionary<string, string>
            {
                ["model.onnx"] = "https://huggingface.co/context4ai/intent-router-onnx/resolve/main/onnx/model.onnx",
                ["tokenizer.json"] = "https://huggingface.co/context4ai/intent-router-onnx/resolve/main/tokenizer.json",
                ["labels.json"] = "https://huggingface.co/context4ai/intent-router-onnx/resolve/main/labels.json"
            },
            "tanaos/tanaos-intent-classifier-v1" => new Dictionary<string, string>
            {
                ["model.onnx"] = "https://huggingface.co/tanaos/tanaos-intent-classifier-v1/resolve/main/model.onnx",
                ["tokenizer.json"] = "https://huggingface.co/tanaos/tanaos-intent-classifier-v1/resolve/main/tokenizer.json"
            },
            "RunsOnBacon/distilbert-intent-classifier-onnx-int8" => new Dictionary<string, string>
            {
                ["model.onnx"] = "https://huggingface.co/RunsOnBacon/distilbert-intent-classifier-onnx-int8/resolve/main/model.onnx",
                ["tokenizer.json"] = "https://huggingface.co/RunsOnBacon/distilbert-intent-classifier-onnx-int8/resolve/main/tokenizer.json"
            },
            _ => null
        };
    }
}
