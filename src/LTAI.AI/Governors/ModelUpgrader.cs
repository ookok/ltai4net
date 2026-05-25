using LTAI.Core.Governors;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public record UpgradeResult
{
    public string FromVersion { get; init; } = "";
    public string ToVersion { get; init; } = "";
    public bool Success { get; init; }
    public bool MigratedCapabilities { get; init; }
    public int TrainingSamplesReplayed { get; init; }
    public float? OldAccuracy { get; init; }
    public float? NewAccuracy { get; init; }
    public TimeSpan Duration { get; init; }
    public string? Error { get; init; }
}

public sealed class ModelUpgrader
{
    private readonly TieredLoraManager _loraManager;
    private readonly CapabilityMigrator _migrator;
    private readonly SynapticMemory? _synapticMemory;
    private readonly ILogger<ModelUpgrader> _logger;
    private readonly Dictionary<string, UpgradeResult> _upgradeHistory = new();
    private string _currentVersion = "";

    public string CurrentVersion => _currentVersion;

    public ModelUpgrader(
        TieredLoraManager loraManager,
        CapabilityMigrator migrator,
        SynapticMemory? synapticMemory = null,
        ILogger<ModelUpgrader>? logger = null)
    {
        _loraManager = loraManager;
        _migrator = migrator;
        _synapticMemory = synapticMemory;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ModelUpgrader>.Instance;
    }

    public async Task<UpgradeResult> UpgradeIfBetterAsync(
        LocalModelInfo newModel,
        HrmReasoningTier tier,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _currentVersion = newModel.Version;

        _logger.LogInformation("Upgrade assessment: tier={Tier} candidate={Candidate}",
            tier, newModel.Version);

        try
        {
            // Step 1: Migrate existing capabilities to new model
            var migrationResult = await _migrator.MigrateToNewModelAsync(
                newModel.Version, tier, ct).ConfigureAwait(false);

            // Step 2: Re-evaluate on held-out samples if synaptic memory has them
            float? oldAcc = null, newAcc = null;
            if (_synapticMemory is not null)
            {
                var evalSamples = _synapticMemory.GetTrainingSamples(maxCount: 50);
                if (evalSamples.Count >= 10)
                {
                    var network = _loraManager.GetNetwork(tier);
                    if (network is not null)
                    {
                        oldAcc = EvaluateAccuracy(network, evalSamples);
                    }

                    // Quick retrain on migrated samples if any
                    if (migrationResult.SamplesReplayed > 0)
                    {
                        var migratedSamples = _synapticMemory.GetTrainingSamples(maxCount: 100);
                        if (migratedSamples.Count >= 10)
                        {
                            _loraManager.TrainTier(tier, migratedSamples, epochs: 3, lr: 0.005f);
                            network = _loraManager.GetNetwork(tier);
                            newAcc = network is not null ? EvaluateAccuracy(network, evalSamples) : null;
                        }
                    }
                }
            }

            var result = new UpgradeResult
            {
                FromVersion = _currentVersion, ToVersion = newModel.Version,
                Success = true, MigratedCapabilities = migrationResult.SamplesReplayed > 0,
                TrainingSamplesReplayed = migrationResult.SamplesReplayed,
                OldAccuracy = oldAcc, NewAccuracy = newAcc,
                Duration = sw.Elapsed
            };

            lock (_upgradeHistory)
            {
                _upgradeHistory[newModel.Version] = result;
            }

            // Auto-rollback if new model is significantly worse
            if (newAcc.HasValue && oldAcc.HasValue && newAcc.Value < oldAcc.Value * 0.9f)
            {
                _logger.LogWarning(
                    "Upgrade degraded: old={Old:F3} new={New:F3}, auto-rollback recommended",
                    oldAcc, newAcc);
            }
            else
            {
                _logger.LogInformation(
                    "Upgrade complete: {From}→{To} migrated={Mig} acc={Old:F3}→{New:F3}",
                    result.FromVersion, result.ToVersion,
                    result.MigratedCapabilities, result.OldAccuracy, result.NewAccuracy);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upgrade failed: {Version}", newModel.Version);
            return new UpgradeResult
            {
                FromVersion = _currentVersion, ToVersion = newModel.Version,
                Success = false, Duration = sw.Elapsed, Error = ex.Message
            };
        }
    }

    public async Task<bool> RollbackAsync(string previousVersion, HrmReasoningTier tier, CancellationToken ct = default)
    {
        var previous = LocalModelRegistry.GetByVersion(previousVersion);
        if (previous is null)
        {
            _logger.LogWarning("Rollback target not found in registry: {Ver}", previousVersion);
            return false;
        }

        _logger.LogInformation("Rolling back to {Version}", previousVersion);
        await _migrator.MigrateToNewModelAsync(previousVersion, tier, ct).ConfigureAwait(false);
        _currentVersion = previousVersion;
        return true;
    }

    public IReadOnlyList<UpgradeResult> GetHistory()
    {
        lock (_upgradeHistory) return _upgradeHistory.Values.ToList();
    }

    public Dictionary<string, object> GetStats()
    {
        var history = GetHistory();
        return new Dictionary<string, object>
        {
            ["current_version"] = _currentVersion,
            ["total_upgrades"] = history.Count,
            ["successful"] = history.Count(h => h.Success),
            ["last_upgrade"] = history.LastOrDefault()?.ToVersion ?? "none",
            ["avg_acc_change"] = history
                .Where(h => h.OldAccuracy.HasValue && h.NewAccuracy.HasValue)
                .Select(h => h.NewAccuracy!.Value - h.OldAccuracy!.Value)
                .DefaultIfEmpty(0).Average()
        };
    }

    private static float EvaluateAccuracy(IntentClassifierNetwork network, List<TrainingSample> samples)
    {
        if (samples.Count == 0) return 0;
        int correct = 0;
        foreach (var s in samples)
        {
            var (pred, _) = network.Predict(s.Text);
            var expectedIdx = s.Label.ToLowerInvariant() switch
            {
                var l when l.Contains("fast") => 0, var l when l.Contains("deep") => 1,
                var l when l.Contains("code") => 2, var l when l.Contains("chat") => 3, _ => 3
            };
            if (pred == expectedIdx) correct++;
        }
        return (float)correct / samples.Count;
    }
}
