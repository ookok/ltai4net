using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed record TrainingResult
{
    public string ModelPath { get; init; } = "";
    public float Accuracy { get; init; }
    public float MacroAccuracy { get; init; }
    public int TrainingSamples { get; init; }
    public TimeSpan TrainingDuration { get; init; }
    public string? ErrorMessage { get; init; }
    public bool Success => string.IsNullOrEmpty(ErrorMessage);
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

            var modelPath = Path.Combine(_modelDirectory, $"intent_classifier_{DateTime.UtcNow:yyyyMMdd_HHmmss}.zip");
            _ml.Model.Save(model, data.Schema, modelPath);

            stopwatch.Stop();

            var result = new TrainingResult
            {
                ModelPath = modelPath,
                Accuracy = (float)metrics.MicroAccuracy,
                MacroAccuracy = (float)metrics.MacroAccuracy,
                TrainingSamples = samples.Count,
                TrainingDuration = stopwatch.Elapsed
            };

            _logger.LogInformation(
                "Intent classifier trained: accuracy={Accuracy:F3}, macro={Macro:F3}, samples={Samples}, time={Time:F1}s",
                result.Accuracy, result.MacroAccuracy, result.TrainingSamples, result.TrainingDuration.TotalSeconds);

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

    public string? GetLatestModelPath()
    {
        if (!Directory.Exists(_modelDirectory))
            return null;

        var modelFiles = Directory.GetFiles(_modelDirectory, "intent_classifier_*.zip")
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
            .FirstOrDefault();

        return modelFiles;
    }
}
