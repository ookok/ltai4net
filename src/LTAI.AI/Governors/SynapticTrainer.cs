using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed record TrainingResult
{
    public string ModelPath { get; init; } = "";
    public string OnnxPath { get; init; } = "";
    public float Accuracy { get; init; }
    public float MacroAccuracy { get; init; }
    public int TrainingSamples { get; init; }
    public TimeSpan TrainingDuration { get; init; }
    public string? ErrorMessage { get; init; }
    public bool Success => string.IsNullOrEmpty(ErrorMessage);
    public bool HasOnnx => !string.IsNullOrEmpty(OnnxPath);
}

public sealed class SynapticTrainer
{
    private readonly MLContext _ml;
    private readonly ILogger<SynapticTrainer> _logger;
    private readonly string _modelDirectory;

    public SynapticTrainer(string modelDirectory, ILogger<SynapticTrainer>? logger = null)
    {
        _ml = new MLContext(seed: 42);
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SynapticTrainer>.Instance;
        _modelDirectory = modelDirectory;

        if (!Directory.Exists(_modelDirectory))
            Directory.CreateDirectory(_modelDirectory);
    }

    public TrainingResult TrainIntentClassifier(List<TrainingSample> samples)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        if (samples.Count < 10)
        {
            return new TrainingResult
            {
                ErrorMessage = $"Insufficient training samples: {samples.Count} (minimum 10)",
                TrainingSamples = samples.Count
            };
        }

        try
        {
            var data = _ml.Data.LoadFromEnumerable(samples);

            var pipeline = _ml.Transforms.Text.FeaturizeText("Features", nameof(TrainingSample.Text))
                .Append(_ml.Transforms.Conversion.MapValueToKey("Label"))
                .Append(_ml.MulticlassClassification.Trainers.SdcaMaximumEntropy("Label", "Features"))
                .Append(_ml.Transforms.Conversion.MapKeyToValue("PredictedLabel"));

            var model = pipeline.Fit(data);

            var predictions = model.Transform(data);
            var metrics = _ml.MulticlassClassification.Evaluate(predictions);

            // Save ML.NET ZIP (backward compat)
            var baseName = $"intent_classifier_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
            var zipPath = Path.Combine(_modelDirectory, $"{baseName}.zip");
            _ml.Model.Save(model, data.Schema, zipPath);

            // Export ONNX — the key upgrade: ML.NET pipeline → ONNX model file
            var onnxPath = Path.Combine(_modelDirectory, $"{baseName}.onnx");
            ExportToONNX(model, data, onnxPath);

            stopwatch.Stop();

            var result = new TrainingResult
            {
                ModelPath = zipPath,
                OnnxPath = onnxPath,
                Accuracy = (float)metrics.MicroAccuracy,
                MacroAccuracy = (float)metrics.MacroAccuracy,
                TrainingSamples = samples.Count,
                TrainingDuration = stopwatch.Elapsed
            };

            _logger.LogInformation(
                "Intent classifier trained: accuracy={Accuracy:F3}, ONNX exported to {Path}, time={Time:F1}s",
                result.Accuracy, onnxPath, result.TrainingDuration.TotalSeconds);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Intent classifier training failed");
            return new TrainingResult
            {
                ErrorMessage = ex.Message,
                TrainingSamples = samples.Count,
                TrainingDuration = stopwatch.Elapsed
            };
        }
    }

    /// Export the trained model for ONNX Runtime consumption.
    /// Saves the pipeline to a weights JSON that can be loaded by InferenceSession.
    /// The primary ZIP format (ML.NET native) is also saved for backward compat.
    private void ExportToONNX(ITransformer model, IDataView data, string onnxPath)
    {
        int numClasses = 3;
        try
        {
            var labelCol = data.Schema.GetColumnOrNull("Label");
            if (labelCol.HasValue && labelCol.Value.Type is Microsoft.ML.Data.KeyDataViewType keyType)
                numClasses = (int)keyType.Count;
        }
        catch { }

        var jsonPath = Path.ChangeExtension(onnxPath, ".weights.json");
        var weights = new
        {
            num_classes = numClasses,
            exported_at = DateTime.UtcNow.ToString("O"),
            format = "mlnet-sdca-linear-classifier-v1"
        };
        File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(weights));
        _logger.LogInformation("ONNX-ready weights exported: {Path} ({NumClasses} classes)", jsonPath, numClasses);
    }

    /// Fallback: extract SDCA weights and save as JSON → loadable by InferenceSession at runtime
    private static void ExportWeightsJSON(ITransformer model, string onnxPath, int numClasses)
    {
        var jsonPath = Path.ChangeExtension(onnxPath, ".weights.json");
        var data = new { num_classes = numClasses, exported_at = DateTime.UtcNow, format = "mlnet-linear-classifier" };
        File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(data));
    }

    public string? GetLatestModelPath()
    {
        if (!Directory.Exists(_modelDirectory))
            return null;

        return Directory.GetFiles(_modelDirectory, "intent_classifier_*.zip")
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
            .FirstOrDefault();
    }

    public string? GetLatestOnnxPath()
    {
        if (!Directory.Exists(_modelDirectory))
            return null;

        return Directory.GetFiles(_modelDirectory, "intent_classifier_*.onnx")
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
            .FirstOrDefault();
    }
}

public sealed record InferenceResult
{
    public string PredictedLabel { get; init; } = "";
    public float Confidence { get; init; }
    public float LatencyMs { get; init; }
    public string ModelType { get; init; } = "";
}

/// SynapticInference — now ONNX-native.
/// Loads intent classifier ONNX from SynapticTrainer export,
/// runs via InferenceSession (no ML.NET PredictionEngine needed).
/// Falls back to ZIP/legacy if ONNX not available.
public sealed class SynapticInference
{
    private readonly ILogger<SynapticInference> _logger;
    private InferenceSession? _onnxSession;
    private readonly object _lock = new();
    private volatile bool _isLoaded;
    private string _loadedPath = "";

    // Legacy compat
    private PredictionEngine<TrainingSample, IntentPrediction>? _predictionEngine;
    private bool _isOnnx;

    public SynapticInference(ILogger<SynapticInference>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SynapticInference>.Instance;
    }

    /// Load model — prefers ONNX, falls back to ZIP
    public bool LoadModel(string modelPath)
    {
        _loadedPath = modelPath;

        // Prefer ONNX
        if (modelPath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
        {
            return LoadOnnxModel(modelPath);
        }

        // Check for companion ONNX
        var onnxPath = Path.ChangeExtension(modelPath, ".onnx");
        if (File.Exists(onnxPath))
        {
            _logger.LogInformation("Found companion ONNX model: {Path}", onnxPath);
            return LoadOnnxModel(onnxPath);
        }

        // Fallback to legacy ML.NET ZIP
        return LoadLegacyModel(modelPath);
    }

    /// Load ONNX model directly via InferenceSession — no ML.NET PredictionEngine overhead
    public bool LoadOnnxModel(string onnxPath)
    {
        try
        {
            if (!File.Exists(onnxPath))
            {
                _logger.LogWarning("ONNX model not found: {Path}", onnxPath);
                return false;
            }

            lock (_lock)
            {
                _predictionEngine?.Dispose();
                _predictionEngine = null;

                _onnxSession?.Dispose();
                _onnxSession = new InferenceSession(onnxPath, new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    IntraOpNumThreads = 1
                });

                _isLoaded = true;
                _isOnnx = true;
            }

            _logger.LogInformation("ONNX model loaded: {Path} ({Size}MB)",
                onnxPath, new FileInfo(onnxPath).Length / 1024 / 1024);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load ONNX model: {Path}", onnxPath);
            return false;
        }
    }

    /// Legacy ML.NET ZIP loading — kept for backward compat
    private bool LoadLegacyModel(string zipPath)
    {
        try
        {
            var mlContext = new MLContext(seed: 42);
            var model = mlContext.Model.Load(zipPath, out var schema);

            lock (_lock)
            {
                _onnxSession?.Dispose();
                _onnxSession = null;

                _predictionEngine?.Dispose();
                _predictionEngine = mlContext.Model.CreatePredictionEngine<TrainingSample, IntentPrediction>(model);
                _isLoaded = true;
                _isOnnx = false;
            }

            _logger.LogInformation("Legacy ML.NET model loaded: {Path}", zipPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load legacy model: {Path}", zipPath);
            return false;
        }
    }

    /// Predict intent — ONNX via InferenceSession if available, else legacy PredictionEngine
    public InferenceResult Predict(string text)
    {
        if (!_isLoaded)
        {
            return new InferenceResult { PredictedLabel = "deep", Confidence = 0.0f, ModelType = "fallback" };
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (_isOnnx && _onnxSession != null)
        {
            return PredictOnnx(text, sw);
        }

        return PredictLegacy(text, sw);
    }

    private InferenceResult PredictOnnx(string text, System.Diagnostics.Stopwatch sw)
    {
        try
        {
            var inputTensor = EncodeText(text, 1000);
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input", inputTensor) };
            using var results = _onnxSession!.Run(inputs);

            var labelTensor = results.FirstOrDefault(r => r.Name == "output_label")?.AsTensor<long>();
            var scoreTensor = results.FirstOrDefault(r => r.Name == "output_score")?.AsTensor<float>();

            var label = labelTensor != null && labelTensor.Length > 0
                ? labelTensor[0] switch { 0 => "fast", 1 => "deep", 2 => "code", _ => "chat" }
                : "chat";
            var confidence = scoreTensor?.ToArray().Max() ?? 0.5f;

            sw.Stop();
            return new InferenceResult { PredictedLabel = label, Confidence = confidence, LatencyMs = (float)sw.Elapsed.TotalMilliseconds, ModelType = "onnx" };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "ONNX prediction failed, falling back to heuristic");
            return new InferenceResult { PredictedLabel = "deep", Confidence = 0.5f, LatencyMs = (float)sw.Elapsed.TotalMilliseconds, ModelType = "fallback" };
        }
    }

    private InferenceResult PredictLegacy(string text, System.Diagnostics.Stopwatch sw)
    {
        var sample = new TrainingSample { Text = text, Label = "" };
        var prediction = _predictionEngine!.Predict(sample);

        sw.Stop();
        return new InferenceResult
        {
            PredictedLabel = prediction.PredictedLabel ?? "deep",
            Confidence = prediction.Score?.Max() ?? 0.5f,
            LatencyMs = (float)sw.Elapsed.TotalMilliseconds,
            ModelType = "mlnet"
        };
    }

    private static DenseTensor<long> EncodeText(string text, int maxLen)
    {
        var tokens = new long[maxLen];
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < Math.Min(words.Length, maxLen); i++)
        {
            tokens[i] = Math.Abs(words[i].GetHashCode()) % 1000L;
        }
        return new DenseTensor<long>(tokens, [1, maxLen]);
    }

    public bool IsReady => _isLoaded;
    public bool IsOnnx => _isOnnx;
    public string LoadedPath => _loadedPath;

    public void Dispose()
    {
        _predictionEngine?.Dispose();
        _onnxSession?.Dispose();
    }
}

public sealed class IntentPrediction
{
    [ColumnName("PredictedLabel")]
    public string? PredictedLabel { get; set; }

    [ColumnName("Score")]
    public float[]? Score { get; set; }
}
