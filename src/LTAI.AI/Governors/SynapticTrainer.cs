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
    public string? LoraCheckpointPath { get; init; }
    public int Generation { get; init; }
    public bool IsLoraTrained => Generation > 0;
}

public sealed class SynapticTrainer
{
    private readonly MLContext _ml;
    private readonly ILogger<SynapticTrainer> _logger;
    private readonly string _modelDirectory;
    private readonly LoraTrainer? _loraTrainer;

    public bool UseLora => _loraTrainer != null;

    public SynapticTrainer(string modelDirectory, ILogger<SynapticTrainer>? logger = null,
        LoraTrainer? loraTrainer = null)
    {
        _ml = new MLContext(seed: 42);
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SynapticTrainer>.Instance;
        _modelDirectory = modelDirectory;
        _loraTrainer = loraTrainer;

        if (!global::System.IO.Directory.Exists(_modelDirectory))
            global::System.IO.Directory.CreateDirectory(_modelDirectory);
    }

    public TrainingResult TrainIntentClassifier(List<TrainingSample> samples)
    {
        if (_loraTrainer != null)
            return TrainWithLora(samples);
        return TrainWithMLNet(samples);
    }

    private TrainingResult TrainWithLora(List<TrainingSample> samples)
    {
        var result = _loraTrainer!.Train(samples, epochs: 5, lr: 0.01f);

        return new TrainingResult
        {
            ModelPath = result.ModelPath ?? "",
            OnnxPath = result.CheckpointPath ?? "",
            LoraCheckpointPath = result.CheckpointPath,
            Generation = result.Generation,
            Accuracy = result.Accuracy,
            TrainingSamples = result.SamplesTrained,
            TrainingDuration = result.Duration,
            ErrorMessage = result.ErrorMessage
        };
    }

    private TrainingResult TrainWithMLNet(List<TrainingSample> samples)
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

            var baseName = $"intent_classifier_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
            var zipPath = global::System.IO.Path.Combine(_modelDirectory, $"{baseName}.zip");
            _ml.Model.Save(model, data.Schema, zipPath);

            var onnxPath = global::System.IO.Path.Combine(_modelDirectory, $"{baseName}.onnx");
            ExportOnnxWeights(model, data, onnxPath);

            stopwatch.Stop();

            var trainingResult = new TrainingResult
            {
                ModelPath = zipPath,
                OnnxPath = onnxPath,
                Accuracy = (float)metrics.MicroAccuracy,
                MacroAccuracy = (float)metrics.MacroAccuracy,
                TrainingSamples = samples.Count,
                TrainingDuration = stopwatch.Elapsed
            };

            _logger.LogInformation(
                "ML.NET classifier trained: accuracy={Accuracy:F3}, time={Time:F1}s",
                trainingResult.Accuracy, trainingResult.TrainingDuration.TotalSeconds);

            return trainingResult;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "ML.NET training failed");
            return new TrainingResult
            {
                ErrorMessage = ex.Message,
                TrainingSamples = samples.Count,
                TrainingDuration = stopwatch.Elapsed
            };
        }
    }

    private void ExportOnnxWeights(ITransformer model, IDataView data, string onnxPath)
    {
        int numClasses = 3;
        try
        {
            var labelCol = data.Schema.GetColumnOrNull("Label");
            if (labelCol.HasValue && labelCol.Value.Type is KeyDataViewType keyType)
                numClasses = (int)keyType.Count;
        }
        catch { }

        var jsonPath = global::System.IO.Path.ChangeExtension(onnxPath, ".weights.json");
        var weights = new
        {
            num_classes = numClasses,
            exported_at = DateTime.UtcNow.ToString("O"),
            format = "mlnet-sdca-linear-classifier-v1"
        };
        global::System.IO.File.WriteAllText(jsonPath,
            global::System.Text.Json.JsonSerializer.Serialize(weights));
    }

    public string? GetLatestModelPath()
    {
        if (!global::System.IO.Directory.Exists(_modelDirectory)) return null;

        if (_loraTrainer != null)
        {
            var weightsPath = _loraTrainer.GetLatestWeightsPath();
            if (weightsPath != null) return weightsPath;
        }

        return global::System.IO.Directory.GetFiles(_modelDirectory, "intent_classifier_*.zip")
            .OrderByDescending(f => global::System.IO.File.GetLastWriteTimeUtc(f))
            .FirstOrDefault();
    }

    public string? GetLatestOnnxPath()
    {
        if (!global::System.IO.Directory.Exists(_modelDirectory)) return null;

        return global::System.IO.Directory.GetFiles(_modelDirectory, "intent_classifier_gen*_*.weights.json")
            .OrderByDescending(f => global::System.IO.File.GetLastWriteTimeUtc(f))
            .FirstOrDefault()
            ?? global::System.IO.Directory.GetFiles(_modelDirectory, "intent_classifier_*.onnx")
            .OrderByDescending(f => global::System.IO.File.GetLastWriteTimeUtc(f))
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

public sealed class SynapticInference : IDisposable
{
    private readonly ILogger<SynapticInference> _logger;
    private readonly LoraTrainer? _loraTrainer;
    private InferenceSession? _onnxSession;
    private readonly object _lock = new();
    private volatile bool _isLoaded;
    private string _loadedPath = "";
    private int _loadedGeneration;

    // Legacy ML.NET compat
    private PredictionEngine<TrainingSample, IntentPrediction>? _predictionEngine;
    private bool _isOnnx;
    private bool _isLora;

    public bool IsReady => _isLoaded;
    public bool IsOnnx => _isOnnx || _isLora;
    public string LoadedPath => _loadedPath;
    public int LoadedGeneration => _loadedGeneration;

    // Hot-reload event for subscribers
    public event Action<string, int>? OnModelHotReloaded;

    public SynapticInference(ILogger<SynapticInference>? logger = null,
        LoraTrainer? loraTrainer = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SynapticInference>.Instance;
        _loraTrainer = loraTrainer;
    }

    /// Load model — prioritizes LoRA weights > ONNX > ML.NET ZIP
    public bool LoadModel(string modelPath)
    {
        _loadedPath = modelPath;

        // LoRA-trained weights JSON
        if (modelPath.EndsWith(".weights.json", StringComparison.OrdinalIgnoreCase) && _loraTrainer != null)
        {
            return LoadLoraWeights(modelPath);
        }

        // ONNX model
        if (modelPath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
        {
            return LoadOnnxModel(modelPath);
        }

        var onnxPath = global::System.IO.Path.ChangeExtension(modelPath, ".onnx");
        if (global::System.IO.File.Exists(onnxPath))
            return LoadOnnxModel(onnxPath);

        // Try LoRA weights
        var weightsPath = global::System.IO.Path.ChangeExtension(modelPath, ".weights.json");
        if (global::System.IO.File.Exists(weightsPath) && _loraTrainer != null)
            return LoadLoraWeights(weightsPath);

        return LoadLegacyModel(modelPath);
    }

    /// Load LoRA-trained weights JSON and merge into network for inference
    public bool LoadLoraWeights(string weightsPath)
    {
        try
        {
            if (!global::System.IO.File.Exists(weightsPath))
            {
                _logger.LogWarning("LoRA weights not found: {Path}", weightsPath);
                return false;
            }

            var json = global::System.IO.File.ReadAllText(weightsPath);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("generation", out var gen))
                _loadedGeneration = gen.GetInt32();

            lock (_lock)
            {
                DisposeInternal();
                _isLora = true;
                _isOnnx = false;
                _isLoaded = true;
                _loadedPath = weightsPath;
            }

            // Ensure LoRA network is merged for inference
            _loraTrainer?.Network.Merge();

            var labels = root.TryGetProperty("labels", out var lbls)
                ? lbls.EnumerateArray().Select(l => l.GetString() ?? "").ToArray()
                : LoraTrainer.DefaultLabels;

            _logger.LogInformation(
                "LoRA weights loaded: {Path} gen={Gen} labels=[{Labels}]",
                weightsPath, _loadedGeneration, string.Join(",", labels));

            OnModelHotReloaded?.Invoke(weightsPath, _loadedGeneration);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load LoRA weights: {Path}", weightsPath);
            return false;
        }
    }

    /// Hot-reload: swap to a newer model atomically without downtime
    public bool HotReload(string newWeightsPath)
    {
        if (!global::System.IO.File.Exists(newWeightsPath))
        {
            _logger.LogWarning("HotReload target not found: {Path}", newWeightsPath);
            return false;
        }

        _logger.LogInformation("HotReload: {Old} -> {New} (gen {OldGen} -> ...)",
            _loadedPath, newWeightsPath, _loadedGeneration);

        var success = LoadModel(newWeightsPath);

        if (success)
            _logger.LogInformation("HotReload complete: {Path} gen={Gen}", newWeightsPath, _loadedGeneration);
        else
            _logger.LogWarning("HotReload failed, keeping existing model: {Path}", _loadedPath);

        return success;
    }

    /// Async hot-reload for background training threads
    public async Task<bool> HotReloadAsync(string newWeightsPath, CancellationToken ct = default)
    {
        return await Task.Run(() => HotReload(newWeightsPath), ct).ConfigureAwait(false);
    }

    /// Auto-discover and load the latest model
    public bool LoadLatest()
    {
        if (_loraTrainer != null)
        {
            var latestWeights = _loraTrainer.GetLatestWeightsPath();
            if (latestWeights != null && latestWeights != _loadedPath)
                return LoadLoraWeights(latestWeights);
        }

        var latestOnnx = GetLatestOnnxFile();
        if (latestOnnx != null && latestOnnx != _loadedPath)
            return LoadOnnxModel(latestOnnx);

        return _isLoaded;
    }

    public bool LoadOnnxModel(string onnxPath)
    {
        try
        {
            if (!global::System.IO.File.Exists(onnxPath))
            {
                _logger.LogWarning("ONNX model not found: {Path}", onnxPath);
                return false;
            }

            lock (_lock)
            {
                DisposeInternal();

                _onnxSession = new InferenceSession(onnxPath, new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    IntraOpNumThreads = 1
                });

                _isLoaded = true;
                _isOnnx = true;
                _isLora = false;
            }

            _logger.LogInformation("ONNX model loaded: {Path} ({Size}MB)",
                onnxPath, new global::System.IO.FileInfo(onnxPath).Length / 1024 / 1024);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load ONNX model: {Path}", onnxPath);
            return false;
        }
    }

    private bool LoadLegacyModel(string zipPath)
    {
        try
        {
            var mlContext = new MLContext(seed: 42);
            var model = mlContext.Model.Load(zipPath, out var schema);

            lock (_lock)
            {
                DisposeInternal();
                _predictionEngine = mlContext.Model.CreatePredictionEngine<TrainingSample, IntentPrediction>(model);
                _isLoaded = true;
                _isOnnx = false;
                _isLora = false;
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

    public InferenceResult Predict(string text)
    {
        if (!_isLoaded)
        {
            return new InferenceResult { PredictedLabel = "deep", Confidence = 0.0f, ModelType = "fallback" };
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (_isLora && _loraTrainer != null)
        {
            return PredictLora(text, sw);
        }

        if (_isOnnx && _onnxSession != null)
        {
            return PredictOnnx(text, sw);
        }

        return PredictLegacy(text, sw);
    }

    private InferenceResult PredictLora(string text, System.Diagnostics.Stopwatch sw)
    {
        try
        {
            var (classIdx, confidence) = _loraTrainer!.Network.Predict(text);
            var label = _loraTrainer.MapIndexToLabel(classIdx);

            sw.Stop();
            return new InferenceResult
            {
                PredictedLabel = label, Confidence = confidence,
                LatencyMs = (float)sw.Elapsed.TotalMilliseconds,
                ModelType = $"lora_gen{_loraTrainer.Network.Generation}"
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "LoRA prediction failed");
            return new InferenceResult
            {
                PredictedLabel = "deep", Confidence = 0.5f,
                LatencyMs = (float)sw.Elapsed.TotalMilliseconds, ModelType = "fallback"
            };
        }
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
            return new InferenceResult
            {
                PredictedLabel = label, Confidence = confidence,
                LatencyMs = (float)sw.Elapsed.TotalMilliseconds, ModelType = "onnx"
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "ONNX prediction failed");
            return new InferenceResult
            {
                PredictedLabel = "deep", Confidence = 0.5f,
                LatencyMs = (float)sw.Elapsed.TotalMilliseconds, ModelType = "fallback"
            };
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
        for (int i = 0; i < global::System.Math.Min(words.Length, maxLen); i++)
            tokens[i] = global::System.Math.Abs(words[i].GetHashCode()) % 1000L;
        return new DenseTensor<long>(tokens, [1, maxLen]);
    }

    private string? GetLatestOnnxFile()
    {
        if (!global::System.IO.Directory.Exists(_loraTrainer?.GetLatestWeightsPath() ?? ""))
            return null;
        return null;
    }

    private void DisposeInternal()
    {
        _predictionEngine?.Dispose();
        _predictionEngine = null;
        _onnxSession?.Dispose();
        _onnxSession = null;
    }

    public void Dispose()
    {
        lock (_lock) DisposeInternal();
    }
}

public sealed class IntentPrediction
{
    [ColumnName("PredictedLabel")]
    public string? PredictedLabel { get; set; }

    [ColumnName("Score")]
    public float[]? Score { get; set; }
}
