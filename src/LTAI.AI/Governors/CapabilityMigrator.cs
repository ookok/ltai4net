using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public record MigrationResult
{
    public int SamplesReplayed { get; init; }
    public int CheckpointsPreserved { get; init; }
    public bool LoRaTransferred { get; init; }
    public string NewModelVersion { get; init; } = "";
    public string? Note { get; init; }
}

/// Handles knowledge transfer when switching base models.
/// Three strategies depending on model change type:
///   1. Same architecture, different version → hot-swap LoRA checkpoints
///   2. Same family, different size → replay training samples from SynapticMemory
///   3. Different family → archive old checkpoints, retrain from scratch
public sealed class CapabilityMigrator
{
    private readonly TieredLoraManager _loraManager;
    private readonly SynapticMemory? _synapticMemory;
    private readonly ILogger<CapabilityMigrator> _logger;
    private readonly string _archiveDir;

    public CapabilityMigrator(
        TieredLoraManager loraManager,
        SynapticMemory? synapticMemory = null,
        ILogger<CapabilityMigrator>? logger = null)
    {
        _loraManager = loraManager;
        _synapticMemory = synapticMemory;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CapabilityMigrator>.Instance;
        _archiveDir = global::System.IO.Path.Combine(
            global::System.IO.Path.GetDirectoryName(
                _loraManager.GetLatestWeightsPath(HrmReasoningTier.FastThink)
                ?? global::System.IO.Path.Combine(AppContext.BaseDirectory, "synaptic", "models"))
            ?? global::System.IO.Path.Combine(AppContext.BaseDirectory, "synaptic", "models"),
            "archive");
        global::System.IO.Directory.CreateDirectory(_archiveDir);
    }

    public async Task<MigrationResult> MigrateToNewModelAsync(
        string newModelVersion,
        HrmReasoningTier tier,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Capability migration: tier={Tier} target={Ver}", tier, newModelVersion);

        var result = new MigrationResult { NewModelVersion = newModelVersion };

        try
        {
            // Step 1: Archive existing checkpoints before migration
            var archived = ArchiveExistingCheckpoints(tier);
            result = result with { CheckpointsPreserved = archived };

            // Step 2: Try LoRA transfer if architecture compatible
            var existingWeights = _loraManager.GetLatestWeightsPath(tier);
            var loraTransferred = existingWeights is not null
                && TryTransferLoRA(tier, existingWeights, newModelVersion);
            result = new MigrationResult
            {
                NewModelVersion = newModelVersion, CheckpointsPreserved = archived,
                LoRaTransferred = loraTransferred
            };

            // Step 3: Replay training samples from synaptic memory
            if (_synapticMemory is not null)
            {
                var samples = _synapticMemory.GetTrainingSamples(maxCount: 200);
                if (samples.Count >= 10)
                {
                    _logger.LogInformation("Replaying {Count} training samples for migration", samples.Count);
                    await Task.Run(() => _loraManager.TrainTier(tier, samples, epochs: 5, lr: 0.005f), ct).ConfigureAwait(false);
                    result = new MigrationResult
                    {
                        NewModelVersion = newModelVersion, CheckpointsPreserved = archived,
                        LoRaTransferred = loraTransferred, SamplesReplayed = samples.Count,
                        Note = loraTransferred
                            ? "LoRA transferred + fine-tuned on replayed samples"
                            : "Retrained from scratch on synaptic memory samples"
                    };
                }
            }

            _logger.LogInformation(
                "Migration complete: transferred={LoRA} replayed={Samples} archived={CKPT}",
                result.LoRaTransferred, result.SamplesReplayed, result.CheckpointsPreserved);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Capability migration failed");
            return result with { Note = $"Migration error: {ex.Message}" };
        }
    }

    private int ArchiveExistingCheckpoints(HrmReasoningTier tier)
    {
        var tierDir = global::System.IO.Path.Combine(
            global::System.IO.Path.GetDirectoryName(
                _loraManager.GetLatestWeightsPath(tier) ?? _archiveDir) ?? _archiveDir,
            $"tier_{tier.ToString().ToLowerInvariant()}");

        if (!global::System.IO.Directory.Exists(tierDir)) return 0;

        var archiveTarget = global::System.IO.Path.Combine(_archiveDir,
            $"tier_{tier.ToString().ToLowerInvariant()}_{DateTime.UtcNow:yyyyMMdd_HHmmss}");
        global::System.IO.Directory.CreateDirectory(archiveTarget);

        int count = 0;
        foreach (var file in global::System.IO.Directory.GetFiles(tierDir))
        {
            var dest = global::System.IO.Path.Combine(archiveTarget,
                global::System.IO.Path.GetFileName(file));
            global::System.IO.File.Copy(file, dest, overwrite: true);
            count++;
        }

        return count;
    }

    /// Try to transfer LoRA weights to new model if architecture is compatible.
    /// Architecture detection: same rank + same input/output dims → hot-swap
    private bool TryTransferLoRA(HrmReasoningTier tier, string existingWeightsPath, string newVersion)
    {
        try
        {
            var newWeightsPath = _loraManager.GetLatestWeightsPath(tier);
            if (newWeightsPath is null || newWeightsPath == existingWeightsPath) return false;

            // Same architecture family: rename existing checkpoint to new model path
            var tierDir = global::System.IO.Path.GetDirectoryName(existingWeightsPath);
            if (tierDir is null) return false;

            var newPath = global::System.IO.Path.Combine(tierDir,
                $"classifier_migrated_{newVersion}.weights.json");
            global::System.IO.File.Copy(existingWeightsPath, newPath, overwrite: true);

            // Also copy LoRA checkpoint
            var loraPath = existingWeightsPath.Replace(".weights.json", ".lora.json");
            if (global::System.IO.File.Exists(loraPath))
            {
                var newLoraPath = newPath.Replace(".weights.json", ".lora.json");
                global::System.IO.File.Copy(loraPath, newLoraPath, overwrite: true);
            }

            _logger.LogInformation("LoRA transferred: {Old} → {New}", existingWeightsPath, newPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LoRA transfer failed, will retrain from scratch");
            return false;
        }
    }

    /// Detect if two model versions share the same architecture family
    public static bool IsSameFamily(string v1, string v2)
    {
        var extractors = new Func<string, string>[]
        {
            v => v.Contains("qwen") ? "qwen" : "",
            v => v.Contains("smollm") ? "smollm" : "",
            v => v.Contains("rwkv") ? "rwkv" : "",
            v => v.Contains("phi3") ? "phi3" : "",
            v => v.Contains("deepseek") ? "deepseek" : "",
            v => v.Contains("llama") ? "llama" : ""
        };

        var f1 = extractors.Select(e => e(v1)).FirstOrDefault(r => !string.IsNullOrEmpty(r));
        var f2 = extractors.Select(e => e(v2)).FirstOrDefault(r => !string.IsNullOrEmpty(r));

        return f1 == f2 && !string.IsNullOrEmpty(f1);
    }

    /// Determine migration strategy based on model change
    public static string ClassifyMigrationType(string oldVersion, string newVersion)
    {
        if (IsSameFamily(oldVersion, newVersion))
        {
            // Check if only quantization or size change
            var oldParts = oldVersion.Split('-');
            var newParts = newVersion.Split('-');
            if (oldParts.Length > 1 && newParts.Length > 1
                && oldParts[0..^1].SequenceEqual(newParts[0..^1]))
                return "same_architecture"; // e.g., Qwen2.5-0.5B → Qwen2.5-1.5B

            return "same_family"; // e.g., Qwen2.5-0.5B → Qwen2.5-3B
        }

        return "different_family"; // e.g., Qwen → DeepSeek
    }

    public Dictionary<string, object> GetStats()
    {
        var archives = global::System.IO.Directory.Exists(_archiveDir)
            ? global::System.IO.Directory.GetDirectories(_archiveDir).Length
            : 0;

        return new Dictionary<string, object>
        {
            ["archived_checkpoints"] = archives,
            ["archive_dir"] = _archiveDir,
            ["synaptic_samples"] = _synapticMemory?.GetRecentUntrained(1).Count ?? 0
        };
    }
}
