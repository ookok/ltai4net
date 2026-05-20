namespace LTAI.Economy;

public sealed record CorrectionResult
{
    public double CorrectedAdvantage { get; init; }
    public double DiscrepancyRatio { get; init; } = 1.0;
    public double StalenessRatio { get; init; } = 1.0;
    public bool DiscrepancyMasked { get; init; }
    public bool StalenessClipped { get; init; }
    public double TokenIsActive { get; init; } = 1.0;
    public double TrainInferMaskValue { get; init; }
}

public sealed record OffPolicyCorrectionConfig
{
    public double DiscrepancyThresholdC { get; set; } = 1.05;
    public double StalenessClipEpsilon { get; set; } = 0.2;
    public bool EnableExactDecomposition { get; set; } = true;
    public double MinimumPreference { get; set; } = 1e-9;
    public double MaskResetThreshold { get; set; } = 0.9;
}

public sealed class OffPolicyCorrector
{
    private readonly OldLogitSnapshotStore _snapshotStore;
    private readonly OffPolicyCorrectionConfig _config;
    private double _trainInferMaskValue = 1.0;
    private int _totalTokens;
    private int _activeTokens;
    private readonly object _lock = new();

    public OffPolicyCorrector(
        OldLogitSnapshotStore snapshotStore,
        OffPolicyCorrectionConfig? config = null)
    {
        _snapshotStore = snapshotStore;
        _config = config ?? new OffPolicyCorrectionConfig();
    }

    public CorrectionResult ComputeCorrectedAdvantage(
        string toolName,
        double rawAdvantage,
        Dictionary<string, double> currentPreferences,
        Dictionary<string, double> inferencePreferences,
        int rolloutVersion)
    {
        if (!_config.EnableExactDecomposition || !_snapshotStore.HasExactOldLogits(rolloutVersion))
            return ApplyFallbackCorrection(rawAdvantage);

        var oldPrefs = _snapshotStore.GetOldPreferences(rolloutVersion);
        if (oldPrefs == null)
            return ApplyFallbackCorrection(rawAdvantage);

        var rd = _snapshotStore.ComputeDiscrepancyRatio(oldPrefs, inferencePreferences, toolName);
        var rs = _snapshotStore.ComputeStalenessRatio(currentPreferences, oldPrefs, toolName);

        bool discrepancyMasked = IsDiscrepancyMasked(rd);
        bool stalenessClipped = IsStalenessClipped(rs, rawAdvantage);

        double active = 1.0;
        if (discrepancyMasked)
            active = 0;
        else if (stalenessClipped)
            active = 0.5 * Math.Abs(rs - 1.0) / _config.StalenessClipEpsilon;

        Interlocked.Increment(ref _totalTokens);
        if (active > 0) Interlocked.Increment(ref _activeTokens);

        lock (_lock)
        {
            _trainInferMaskValue = _totalTokens > 0
                ? (double)_activeTokens / _totalTokens
                : 1.0;

            if (_trainInferMaskValue < _config.MaskResetThreshold)
            {
                _totalTokens = 0;
                _activeTokens = 0;
                _trainInferMaskValue = 1.0;
            }
        }

        var correctedAdvantage = rawAdvantage * rd * (stalenessClipped
            ? ClipRatio(rs, rawAdvantage)
            : rs);

        correctedAdvantage = Math.Clamp(correctedAdvantage, -10, 10);

        return new CorrectionResult
        {
            CorrectedAdvantage = correctedAdvantage,
            DiscrepancyRatio = rd,
            StalenessRatio = rs,
            DiscrepancyMasked = discrepancyMasked,
            StalenessClipped = stalenessClipped,
            TokenIsActive = active,
            TrainInferMaskValue = _trainInferMaskValue
        };
    }

    public (double groupAdvantage, List<CorrectionResult> corrections) ComputeGroupCorrectedAdvantage(
        IReadOnlyList<(string toolName, double rawAdvantage, Dictionary<string, double> currentPrefs,
            Dictionary<string, double> inferencePrefs, int rolloutVersion)> entries)
    {
        var corrections = new List<CorrectionResult>();
        double totalAdvantage = 0;
        int activeCount = 0;

        foreach (var entry in entries)
        {
            var corr = ComputeCorrectedAdvantage(
                entry.toolName, entry.rawAdvantage,
                entry.currentPrefs, entry.inferencePrefs,
                entry.rolloutVersion);

            corrections.Add(corr);

            if (corr.TokenIsActive > 0)
            {
                totalAdvantage += corr.CorrectedAdvantage;
                activeCount++;
            }
        }

        var groupAdvantage = activeCount > 0
            ? totalAdvantage / activeCount
            : 0;

        return (groupAdvantage, corrections);
    }

    public bool IsDiscrepancyMasked(double rd)
    {
        var c = _config.DiscrepancyThresholdC;
        return rd < 1.0 / c || rd > c;
    }

    public bool IsStalenessClipped(double rs, double advantage)
    {
        var eps = _config.StalenessClipEpsilon;

        if (advantage > 0 && rs > 1 + eps)
            return true;
        if (advantage < 0 && rs < 1 - eps)
            return true;

        return false;
    }

    public double ClipRatio(double rs, double advantage)
    {
        var eps = _config.StalenessClipEpsilon;

        if (advantage > 0)
            return Math.Min(rs, 1 + eps);
        if (advantage < 0)
            return Math.Max(rs, 1 - eps);

        return rs;
    }

    private static CorrectionResult ApplyFallbackCorrection(double rawAdvantage)
    {
        return new CorrectionResult
        {
            CorrectedAdvantage = rawAdvantage,
            DiscrepancyRatio = 1.0,
            StalenessRatio = 1.0,
            DiscrepancyMasked = false,
            StalenessClipped = false,
            TokenIsActive = 1.0
        };
    }

    public double TrainInferMaskValue => _trainInferMaskValue;

    public void ResetMaskTracking()
    {
        Interlocked.Exchange(ref _totalTokens, 0);
        Interlocked.Exchange(ref _activeTokens, 0);
        lock (_lock) { _trainInferMaskValue = 1.0; }
    }

    public Dictionary<string, object> GetStats() => new()
    {
        ["train_infer_mask"] = Math.Round(_trainInferMaskValue, 3),
        ["total_tokens_processed"] = _totalTokens,
        ["active_token_fraction"] = _totalTokens > 0
            ? Math.Round((double)_activeTokens / _totalTokens, 3)
            : 0,
        ["config"] = new
        {
            discrepancy_threshold = _config.DiscrepancyThresholdC,
            staleness_clip_epsilon = _config.StalenessClipEpsilon,
            mask_reset_threshold = _config.MaskResetThreshold
        }
    };
}
