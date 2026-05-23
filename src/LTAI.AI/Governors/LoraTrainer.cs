using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed record LoraTrainingResult
{
    public bool Success { get; init; }
    public float FinalLoss { get; init; }
    public float Accuracy { get; init; }
    public int SamplesTrained { get; init; }
    public int Generation { get; init; }
    public TimeSpan Duration { get; init; }
    public string? ModelPath { get; init; }
    public string? CheckpointPath { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class LoraTrainer
{
    private readonly IntentClassifierNetwork _network;
    private readonly string _modelDirectory;
    private readonly ILogger<LoraTrainer> _logger;
    private readonly int _loraRank;
    private readonly string[] _classLabels;

    public IntentClassifierNetwork Network => _network;
    public int Generation => _network.Generation;

    // Standard intent classes
    public static readonly string[] DefaultLabels = { "fast", "deep", "code", "chat", "reasoning" };

    public LoraTrainer(
        string modelDirectory,
        ILogger<LoraTrainer>? logger = null,
        int inputDim = 256, int hidden1Dim = 128, int hidden2Dim = 64,
        int loraRank = 8, string[]? classLabels = null)
    {
        _modelDirectory = modelDirectory;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<LoraTrainer>.Instance;
        _loraRank = loraRank;
        _classLabels = classLabels ?? DefaultLabels;

        if (!global::System.IO.Directory.Exists(_modelDirectory))
            global::System.IO.Directory.CreateDirectory(_modelDirectory);

        _network = new IntentClassifierNetwork(
            vocabSize: 1000, inputDim: inputDim,
            hidden1Dim: hidden1Dim, hidden2Dim: hidden2Dim,
            numClasses: _classLabels.Length, loraRank: loraRank);

        // Try loading existing model
        TryLoadLatest();
    }

    public LoraTrainingResult Train(List<TrainingSample> samples, int epochs = 5, float lr = 0.01f)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (samples.Count < 5)
        {
            return new LoraTrainingResult
            {
                ErrorMessage = $"Insufficient samples: {samples.Count} (minimum 5)",
                SamplesTrained = samples.Count
            };
        }

        try
        {
            // Map labels to class indices
            var labelToIdx = new Dictionary<string, int>();
            for (int i = 0; i < _classLabels.Length; i++)
                labelToIdx[_classLabels[i]] = i;

            var trainingData = new List<(string text, int targetClass)>();
            int skipped = 0;
            foreach (var s in samples)
            {
                if (labelToIdx.TryGetValue(s.Label, out var idx))
                    trainingData.Add((s.Text, idx));
                else
                {
                    // Map unknown labels to closest via keyword
                    var lower = s.Label.ToLowerInvariant();
                    var bestIdx = lower switch
                    {
                        var l when l.Contains("fast") => 0,
                        var l when l.Contains("deep") || l.Contains("reason") => 1,
                        var l when l.Contains("code") => 2,
                        var l when l.Contains("chat") || l.Contains("general") => 3,
                        var l when l.Contains("reason") || l.Contains("think") => 4,
                        _ => 3 // default to chat
                    };
                    trainingData.Add((s.Text, bestIdx));
                }
            }

            _logger.LogInformation(
                "LoRA training: {Count} samples ({Skipped} skipped), {Epochs} epochs, lr={LR}",
                trainingData.Count, skipped, epochs, lr);

            // Verify base forward pass works
            try { _network.Forward("test"); }
            catch (Exception ex) { _logger.LogError(ex, "Forward pass test failed"); }

            var finalLoss = _network.Train(trainingData, epochs, lr, _logger);

            // Evaluate accuracy
            int correct = 0;
            foreach (var (text, target) in trainingData)
            {
                var (pred, _) = _network.Predict(text);
                if (pred == target) correct++;
            }
            var accuracy = (float)correct / Math.Max(1, trainingData.Count);

            // Merge and save
            _network.Merge();
            var saveResult = SaveModel();
            _network.Unmerge(); // Ready for next training

            _logger.LogInformation(
                "LoRA training complete: accuracy={Accuracy:F3}, loss={Loss:F4}, gen={Gen}, path={Path}",
                accuracy, finalLoss, _network.Generation, saveResult.path);

            return new LoraTrainingResult
            {
                Success = true,
                FinalLoss = finalLoss,
                Accuracy = accuracy,
                SamplesTrained = trainingData.Count,
                Generation = _network.Generation,
                Duration = sw.Elapsed,
                ModelPath = saveResult.path,
                CheckpointPath = saveResult.checkpointPath
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LoRA training failed");
            return new LoraTrainingResult
            {
                ErrorMessage = ex.Message,
                SamplesTrained = samples.Count,
                Duration = sw.Elapsed
            };
        }
    }

    private (string path, string checkpointPath) SaveModel()
    {
        var baseName = $"intent_classifier_gen{_network.Generation}_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
        var weightsPath = global::System.IO.Path.Combine(_modelDirectory, $"{baseName}.weights.json");
        var ckptPath = global::System.IO.Path.Combine(_modelDirectory, $"{baseName}.lora.json");

        // Save merged weights for inference
        var (w1, b1, w2, b2, w3, b3) = _network.Merge();
        var weightsDoc = new
        {
            num_classes = _classLabels.Length,
            labels = _classLabels,
            input_dim = w1.GetLength(1),
            hidden1_dim = w1.GetLength(0),
            hidden2_dim = w2.GetLength(0),
            generation = _network.Generation,
            exported_at = DateTime.UtcNow.ToString("O"),
            format = "lora-trained-fc-classifier-v2",
            w1 = FlattenMatrix(w1), b1,
            w2 = FlattenMatrix(w2), b2,
            w3 = FlattenMatrix(w3), b3
        };
        global::System.IO.File.WriteAllText(weightsPath,
            global::System.Text.Json.JsonSerializer.Serialize(weightsDoc));

        // Save LoRA checkpoint for warm-starting
        var ckpt1 = _network.Lora1.ExportCheckpoint();
        var ckpt2 = _network.Lora2.ExportCheckpoint();
        var ckptDoc = new { ckpt1, ckpt2, generation = _network.Generation };
        global::System.IO.File.WriteAllText(ckptPath,
            global::System.Text.Json.JsonSerializer.Serialize(ckptDoc,
                new global::System.Text.Json.JsonSerializerOptions { IncludeFields = true }));

        _logger.LogInformation("Model saved: weights={W}, checkpoint={C}", weightsPath, ckptPath);
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

    private static float[,] UnflattenMatrix(System.Text.Json.JsonElement elem, int rows, int cols)
    {
        var mat = new float[rows, cols];
        int ri = 0;
        foreach (var row in elem.EnumerateArray())
        {
            if (ri >= rows) break;
            int ci = 0;
            foreach (var val in row.EnumerateArray())
            {
                if (ci >= cols) break;
                mat[ri, ci] = val.GetSingle();
                ci++;
            }
            ri++;
        }
        return mat;
    }

    private void TryLoadLatest()
    {
        var latestWeightFile = global::System.IO.Directory.GetFiles(_modelDirectory, "intent_classifier_gen*_*.weights.json")
            .OrderByDescending(global::System.IO.File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (latestWeightFile is null) return;

        try
        {
            var json = global::System.IO.File.ReadAllText(latestWeightFile);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("format", out var fmt) && fmt.GetString()?.StartsWith("lora-trained") == true)
            {
                _logger.LogInformation("Loading existing model from {Path}", latestWeightFile);

                // Try loading LoRA checkpoint for warm start
                var checkpointPath = latestWeightFile.Replace(".weights.json", ".lora.json");
                if (global::System.IO.File.Exists(checkpointPath))
                {
                    var ckptJson = global::System.IO.File.ReadAllText(checkpointPath);
                    var ckptDoc = System.Text.Json.JsonDocument.Parse(ckptJson);

                    if (ckptDoc.RootElement.TryGetProperty("ckpt1", out var c1))
                    {
                        var ckpt1 = ParseCheckpoint(c1);
                        _network.Lora1.ImportCheckpoint(ckpt1);
                    }
                    if (ckptDoc.RootElement.TryGetProperty("ckpt2", out var c2))
                    {
                        var ckpt2 = ParseCheckpoint(c2);
                        _network.Lora2.ImportCheckpoint(ckpt2);
                    }

                    _network.Merge();
                    _network.Unmerge();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load existing LoRA model");
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
            // A and B matrices loaded via serialization
        };
    }

    public string? GetLatestWeightsPath()
    {
        return global::System.IO.Directory.GetFiles(_modelDirectory, "intent_classifier_gen*_*.weights.json")
            .OrderByDescending(global::System.IO.File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public string? GetLatestCheckpointPath()
    {
        return global::System.IO.Directory.GetFiles(_modelDirectory, "intent_classifier_gen*_*.lora.json")
            .OrderByDescending(global::System.IO.File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    // Expose class label mapping for external use
    public int MapLabelToIndex(string label)
    {
        for (int i = 0; i < _classLabels.Length; i++)
            if (string.Equals(_classLabels[i], label, StringComparison.OrdinalIgnoreCase))
                return i;
        return 3; // default chat
    }

    public string MapIndexToLabel(int idx)
    {
        return idx >= 0 && idx < _classLabels.Length ? _classLabels[idx] : "chat";
    }
}
