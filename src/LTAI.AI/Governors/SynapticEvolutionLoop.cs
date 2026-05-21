using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed record EvolutionMetrics
{
    public int TotalTrainings { get; init; }
    public int TotalExperiences { get; init; }
    public float LatestAccuracy { get; init; }
    public DateTime LastTrainingAt { get; init; }
    public string ModelVersion { get; init; } = "";
}

public sealed class SynapticEvolutionLoop : BackgroundService
{
    private readonly SynapticMemory _memory;
    private readonly SynapticTrainer _trainer;
    private readonly SynapticInference _inference;
    private readonly CellAIRegistry? _cellRegistry;
    private readonly ILogger<SynapticEvolutionLoop> _logger;
    private readonly TimeSpan _checkInterval;
    private readonly int _minSamplesForTraining;
    private EvolutionMetrics _metrics;

    public EvolutionMetrics Metrics => _metrics;

    public SynapticEvolutionLoop(
        SynapticMemory memory,
        SynapticTrainer trainer,
        SynapticInference inference,
        CellAIRegistry? cellRegistry = null,
        ILogger<SynapticEvolutionLoop>? logger = null,
        TimeSpan? checkInterval = null,
        int minSamplesForTraining = 20)
    {
        _memory = memory;
        _trainer = trainer;
        _inference = inference;
        _cellRegistry = cellRegistry;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SynapticEvolutionLoop>.Instance;
        _checkInterval = checkInterval ?? TimeSpan.FromMinutes(5);
        _minSamplesForTraining = minSamplesForTraining;
        _metrics = new EvolutionMetrics();

        _logger.LogInformation(
            "SynapticEvolutionLoop initialized: interval={Interval}s, minSamples={MinSamples}",
            _checkInterval.TotalSeconds, _minSamplesForTraining);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SynapticEvolutionLoop started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EvolutionCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Evolution cycle failed");
            }

            await Task.Delay(_checkInterval, stoppingToken);
        }

        _logger.LogInformation("SynapticEvolutionLoop stopped");
    }

    private async Task EvolutionCycleAsync(CancellationToken ct)
    {
        if (_cellRegistry != null)
        {
            await TrainCellsAsync(ct);
            return;
        }

        var untrained = _memory.GetRecentUntrained(_minSamplesForTraining * 2);

        if (untrained.Count < _minSamplesForTraining)
        {
            _logger.LogDebug(
                "Insufficient untrained experiences: {Count}/{Min}",
                untrained.Count, _minSamplesForTraining);
            return;
        }

        _logger.LogInformation(
            "Starting evolution cycle: {Count} untrained experiences", untrained.Count);

        var samples = untrained
            .Select(exp => new TrainingSample
            {
                Text = exp.Query,
                Label = exp.Label,
                Weight = exp.Reward
            })
            .ToList();

        var result = _trainer.TrainIntentClassifier(samples);

        if (!result.Success)
        {
            _logger.LogWarning("Training failed: {Error}", result.ErrorMessage);
            return;
        }

        var modelPath = _trainer.GetLatestModelPath();
        if (modelPath != null && _inference.LoadModel(modelPath))
        {
            var ids = untrained.Select(e => e.Id).ToList();
            _memory.MarkAsTrained(ids);

            _metrics = new EvolutionMetrics
            {
                TotalTrainings = _metrics.TotalTrainings + 1,
                TotalExperiences = _memory.ExperienceCount,
                LatestAccuracy = result.Accuracy,
                LastTrainingAt = DateTime.UtcNow,
                ModelVersion = Path.GetFileNameWithoutExtension(modelPath)
            };

            _logger.LogInformation(
                "Evolution complete: accuracy={Accuracy:F3}, samples={Samples}, time={Time:F1}s, totalTrainings={Trainings}",
                result.Accuracy, result.TrainingSamples, result.TrainingDuration.TotalSeconds, _metrics.TotalTrainings);
        }
        else
        {
            _logger.LogWarning("Model loaded failed after successful training");
        }
    }

    private async Task TrainCellsAsync(CancellationToken ct)
    {
        var domains = new[] { "code", "math", "science", "language", "system", "creative", "greeting" };
        var trainedCount = 0;

        foreach (var domain in domains)
        {
            try
            {
                var success = await _cellRegistry.TrainCellAsync(domain, ct);
                if (success)
                    trainedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cell training failed for {Domain}", domain);
            }
        }

        if (trainedCount > 0)
        {
            _logger.LogInformation("Cell evolution complete: {Count}/{Total} cells trained",
                trainedCount, domains.Length);
        }
    }

    public async Task ForceEvolutionAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Forced evolution triggered");
        await EvolutionCycleAsync(ct);
    }
}
