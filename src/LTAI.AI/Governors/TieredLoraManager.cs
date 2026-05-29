using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public record TieredTrainingResult
{
    public HrmReasoningTier Tier { get; init; }
    public bool Success { get; init; }
    public float FinalLoss { get; init; }
    public float Accuracy { get; init; }
    public int SamplesTrained { get; init; }
    public int Generation { get; init; }
    public TimeSpan Duration { get; init; }
    public string? WeightsPath { get; init; }
    public string? CheckpointPath { get; init; }
    public string? ErrorMessage { get; init; }
}

/// Manages tier-specific LoRA networks for HRM (Hierarchical Reasoning Model).
/// Each reasoning tier gets its own IntentClassifierNetwork with appropriate rank:
///   Fast: rank-8 LoRA, 256→128→64→N (covers all L1 queries; merges previous FastThink+DeepThink)
///   Deep: no LoRA (uses L2 cloud model)
public sealed class TieredLoraManager
{
    private readonly Dictionary<HrmReasoningTier, IntentClassifierNetwork> _networks = new();
    private readonly string _modelDirectory;
    private readonly ILogger<TieredLoraManager> _logger;
    private readonly AdaptiveDepthController _depthController;
    private readonly string[] _classLabels;

    public static readonly string[] DefaultLabels = { "fast", "deep", "code", "chat", "reasoning" };

    // Tier-specific configs
    // L0/Reflex/Escalate removed — only Fast (L1) and Deep (L2) remain.
    // Fast uses rank-8 (merged previous FastThink rank-4 + DeepThink rank-8).
    private static readonly Dictionary<HrmReasoningTier, (int rank, bool enabled)> TierConfig = new()
    {
        [HrmReasoningTier.Fast] = (8, true),
        [HrmReasoningTier.Deep] = (0, false)
    };

    public TieredLoraManager(
        string modelDirectory,
        AdaptiveDepthController depthController,
        ILogger<TieredLoraManager>? logger = null,
        string[]? classLabels = null)
    {
        _modelDirectory = modelDirectory;
        _depthController = depthController;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TieredLoraManager>.Instance;
        _classLabels = classLabels ?? DefaultLabels;

        if (!global::System.IO.Directory.Exists(_modelDirectory))
            global::System.IO.Directory.CreateDirectory(_modelDirectory);

        InitializeNetworks();
        LoadLatestCheckpoints();
    }

    private void InitializeNetworks()
    {
        foreach (var (tier, (rank, enabled)) in TierConfig)
        {
            if (!enabled) continue;
            _networks[tier] = new IntentClassifierNetwork(
                vocabSize: 1000, inputDim: 256,
                hidden1Dim: 128, hidden2Dim: 64,
                numClasses: _classLabels.Length,
                loraRank: rank);
            _logger.LogInformation("TieredLora: {Tier} initialized (rank={Rank})", tier, rank);
        }
    }

    public IntentClassifierNetwork? GetNetwork(HrmReasoningTier tier)
        => _networks.GetValueOrDefault(tier);

    public HrmReasoningTier ClassifyTier(string query)
    {
        var decision = _depthController.Decide(query);
        return decision.Tier;
    }

    public TieredTrainingResult TrainTier(
        HrmReasoningTier tier,
        List<TrainingSample> samples,
        int epochs = 5, float lr = 0.01f)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (!TierConfig.TryGetValue(tier, out var config) || !config.enabled)
        {
            return new TieredTrainingResult
            {
                Tier = tier, ErrorMessage = $"Tier {tier} does not support training"
            };
        }

        if (!_networks.TryGetValue(tier, out var network))
        {
            return new TieredTrainingResult
            {
                Tier = tier, ErrorMessage = $"No network for tier {tier}"
            };
        }

        if (samples.Count < 5)
        {
            return new TieredTrainingResult
            {
                Tier = tier, ErrorMessage = $"Insufficient samples: {samples.Count}",
                SamplesTrained = samples.Count
            };
        }

        try
        {
            var labelToIdx = new Dictionary<string, int>();
            for (int i = 0; i < _classLabels.Length; i++)
                labelToIdx[_classLabels[i]] = i;

            var trainingData = new List<(string text, int targetClass)>();
            foreach (var s in samples)
            {
                var idx = MapLabel(s, labelToIdx);
                trainingData.Add((s.Text, idx));
            }

            _logger.LogInformation(
                "TieredLora training: tier={Tier} rank={Rank} samples={Count} epochs={Epochs}",
                tier, config.rank, trainingData.Count, epochs);

            var finalLoss = network.Train(trainingData, epochs, lr, _logger);

            // Evaluate
            int correct = 0;
            foreach (var (text, target) in trainingData)
            {
                var (pred, _) = network.Predict(text);
                if (pred == target) correct++;
            }
            var accuracy = (float)correct / Math.Max(1, trainingData.Count);

            // Merge and save
            network.Merge();
            var saveResult = SaveTierModel(tier, network);
            network.Unmerge();

            return new TieredTrainingResult
            {
                Tier = tier, Success = true,
                FinalLoss = finalLoss, Accuracy = accuracy,
                SamplesTrained = trainingData.Count,
                Generation = network.Generation,
                Duration = sw.Elapsed,
                WeightsPath = saveResult.weights, CheckpointPath = saveResult.checkpoint
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TieredLora training failed for {Tier}", tier);
            return new TieredTrainingResult
            {
                Tier = tier, ErrorMessage = ex.Message,
                SamplesTrained = samples.Count, Duration = sw.Elapsed
            };
        }
    }

    private static int MapLabel(TrainingSample sample, Dictionary<string, int> labelToIdx)
    {
        if (labelToIdx.TryGetValue(sample.Label, out var idx))
            return idx;

        var lower = sample.Label.ToLowerInvariant();
        return lower switch
        {
            var l when l.Contains("fast") || l.Contains("reflex") => 0,
            var l when l.Contains("deep") || l.Contains("reason") => 1,
            var l when l.Contains("code") => 2,
            var l when l.Contains("chat") || l.Contains("general") => 3,
            var l when l.Contains("reason") || l.Contains("think") => 4,
            _ => 3
        };
    }

    private (string weights, string checkpoint) SaveTierModel(
        HrmReasoningTier tier, IntentClassifierNetwork network)
    {
        var tierDir = global::System.IO.Path.Combine(_modelDirectory, $"tier_{tier.ToString().ToLowerInvariant()}");
        global::System.IO.Directory.CreateDirectory(tierDir);

        var baseName = $"classifier_gen{network.Generation}_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
        var weightsPath = global::System.IO.Path.Combine(tierDir, $"{baseName}.weights.json");
        var ckptPath = global::System.IO.Path.Combine(tierDir, $"{baseName}.lora.json");

        var (w1, b1, w2, b2, w3, b3) = network.Merge();
        var doc = new
        {
            tier = tier.ToString(), num_classes = _classLabels.Length,
            labels = _classLabels, generation = network.Generation,
            exported_at = DateTime.UtcNow.ToString("O"),
            format = "tiered-lora-classifier-v1",
            w1 = FlattenMatrix(w1), b1, w2 = FlattenMatrix(w2), b2,
            w3 = FlattenMatrix(w3), b3
        };
        global::System.IO.File.WriteAllText(weightsPath,
            global::System.Text.Json.JsonSerializer.Serialize(doc));

        // LoRA checkpoint
        var ckpt1 = network.Lora1.ExportCheckpoint();
        var ckpt2 = network.Lora2.ExportCheckpoint();
        global::System.IO.File.WriteAllText(ckptPath,
            global::System.Text.Json.JsonSerializer.Serialize(
                new { ckpt1, ckpt2, tier = tier.ToString(), gen = network.Generation },
                new global::System.Text.Json.JsonSerializerOptions { IncludeFields = true }));

        _logger.LogInformation("TieredLora saved: tier={Tier} path={Path}", tier, weightsPath);
        return (weightsPath, ckptPath);
    }

    private static object[] FlattenMatrix(float[,] mat)
    {
        var rows = mat.GetLength(0);
        var cols = mat.GetLength(1);
        var flat = new object[rows];
        for (int i = 0; i < rows; i++)
        {
            var row = new float[cols];
            for (int j = 0; j < cols; j++) row[j] = mat[i, j];
            flat[i] = row;
        }
        return flat;
    }

    private void LoadLatestCheckpoints()
    {
        foreach (var (tier, (_, enabled)) in TierConfig)
        {
            if (!enabled) continue;
            var tierDir = global::System.IO.Path.Combine(_modelDirectory, $"tier_{tier.ToString().ToLowerInvariant()}");
            if (!global::System.IO.Directory.Exists(tierDir)) continue;

            var latest = global::System.IO.Directory.GetFiles(tierDir, "classifier_gen*_*.lora.json")
                .OrderByDescending(global::System.IO.File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            if (latest is null) continue;

            try
            {
                var json = global::System.IO.File.ReadAllText(latest);
                var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (_networks.TryGetValue(tier, out var network)
                    && root.TryGetProperty("ckpt1", out var c1)
                    && root.TryGetProperty("ckpt2", out var c2))
                {
                    network.Lora1.ImportCheckpoint(ParseCheckpoint(c1));
                    network.Lora2.ImportCheckpoint(ParseCheckpoint(c2));
                    network.Merge();
                    network.Unmerge();
                    _logger.LogInformation("TieredLora loaded: tier={Tier} from {Path}", tier, latest);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load checkpoint for {Tier}", tier);
            }
        }
    }

    private static LoraCheckpoint ParseCheckpoint(System.Text.Json.JsonElement elem)
    {
        return new LoraCheckpoint
        {
            InputDim = elem.GetProperty("InputDim").GetInt32(),
            OutputDim = elem.GetProperty("OutputDim").GetInt32(),
            Rank = elem.GetProperty("Rank").GetInt32(),
            Scale = elem.GetProperty("Scale").GetSingle()
        };
    }

    /// Train all enabled tiers with their respective samples
    public async Task<List<TieredTrainingResult>> TrainAllTiersAsync(
        Dictionary<HrmReasoningTier, List<TrainingSample>> tieredSamples,
        CancellationToken ct = default)
    {
        var results = new List<TieredTrainingResult>();

        foreach (var (tier, samples) in tieredSamples)
        {
            ct.ThrowIfCancellationRequested();
            var result = await Task.Run(() => TrainTier(tier, samples), ct).ConfigureAwait(false);
            results.Add(result);
        }

        return results;
    }

    /// Get the best model path for a given tier
    public string? GetLatestWeightsPath(HrmReasoningTier tier)
    {
        var tierDir = global::System.IO.Path.Combine(_modelDirectory, $"tier_{tier.ToString().ToLowerInvariant()}");
        if (!global::System.IO.Directory.Exists(tierDir)) return null;

        return global::System.IO.Directory.GetFiles(tierDir, "classifier_gen*_*.weights.json")
            .OrderByDescending(global::System.IO.File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public Dictionary<string, object> GetStats()
    {
        var stats = new Dictionary<string, object>();
        foreach (var (tier, network) in _networks)
        {
            stats[tier.ToString()] = new
            {
                generation = network.Generation,
                total_trained = network.TotalSamplesTrained,
                rank = TierConfig[tier].rank,
                classes = _classLabels.Length
            };
        }
        return stats;
    }
}
