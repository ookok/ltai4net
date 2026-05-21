using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed record InferenceResult
{
    public string PredictedLabel { get; init; } = "";
    public float Confidence { get; init; }
    public float LatencyMs { get; init; }
    public string ModelType { get; init; } = "";
}

public sealed class SynapticInference
{
    private readonly ILogger<SynapticInference> _logger;
    private PredictionEngine<TrainingSample, IntentPrediction>? _predictionEngine;
    private readonly object _lock = new();
    private volatile bool _isLoaded;

    public SynapticInference(ILogger<SynapticInference>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SynapticInference>.Instance;
    }

    public bool LoadModel(string modelPath)
    {
        try
        {
            var mlContext = new MLContext(seed: 42);
            var model = mlContext.Model.Load(modelPath, out var schema);

            lock (_lock)
            {
                _predictionEngine?.Dispose();
                _predictionEngine = mlContext.Model.CreatePredictionEngine<TrainingSample, IntentPrediction>(model);
                _isLoaded = true;
            }

            _logger.LogInformation("SynapticInference model loaded: {Path}", modelPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load model: {Path}", modelPath);
            return false;
        }
    }

    public InferenceResult Predict(string text)
    {
        if (!_isLoaded || _predictionEngine == null)
        {
            return new InferenceResult
            {
                PredictedLabel = "deep",
                Confidence = 0.0f,
                ModelType = "fallback"
            };
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var sample = new TrainingSample { Text = text, Label = "" };
        var prediction = _predictionEngine.Predict(sample);

        stopwatch.Stop();

        return new InferenceResult
        {
            PredictedLabel = prediction.PredictedLabel ?? "deep",
            Confidence = prediction.Score?.Max() ?? 0.5f,
            LatencyMs = (float)stopwatch.Elapsed.TotalMilliseconds,
            ModelType = "mlnet"
        };
    }

    public bool IsReady => _isLoaded && _predictionEngine != null;

    public void Dispose()
    {
        _predictionEngine?.Dispose();
    }
}

public sealed class IntentPrediction
{
    [ColumnName("PredictedLabel")]
    public string? PredictedLabel { get; set; }

    [ColumnName("Score")]
    public float[]? Score { get; set; }
}
