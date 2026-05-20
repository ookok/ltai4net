namespace LTAI.Economy;

public sealed record EwmaCorrectionResult
{
    public double CorrectedAdvantage { get; init; }
    public double DiscrepancyRatio { get; init; } = 1.0;
    public double StalenessRatio { get; init; } = 1.0;
    public bool IsMasked { get; init; }
    public bool IsClipped { get; init; }
    public double TokenIsActive { get; init; } = 1.0;
    public double EwmaProxQuality { get; init; } = 1.0;
    public bool WasReset { get; init; }
}

public sealed record PpoEwmaConfig
{
    public double DecayBeta { get; set; }
    public double MaskResetThreshold { get; set; } = 0.9;
    public double DiscrepancyThresholdC { get; set; } = 1.05;
    public double StalenessClipEpsilon { get; set; } = 0.2;
    public int ExpectedStaleWindow { get; set; } = 3;
    public bool EnableAutoReset { get; set; } = true;
    public double MinimumPreference { get; set; } = 1e-9;

    public static PpoEwmaConfig Default => new()
    {
        DecayBeta = 3.0 / 5.0
    };

    public static PpoEwmaConfig ForStaleWindow(int windowSize) => new()
    {
        DecayBeta = (double)windowSize / (windowSize + 2),
        ExpectedStaleWindow = windowSize
    };
}

public sealed class PpoEwmaCorrector
{
    private readonly OldLogitSnapshotStore _snapshotStore;
    private readonly OffPolicyCorrector _offPolicyCorrector;
    private readonly PpoEwmaConfig _config;

    private readonly Dictionary<string, double> _ewmaPreferences = new();
    private int _ewmaUpdateCount;
    private double _trainInferMaskValue = 1.0;
    private int _totalTokens;
    private int _activeTokens;
    private int _resetCount;
    private readonly object _ewmaLock = new();

    public PpoEwmaCorrector(
        OldLogitSnapshotStore snapshotStore,
        OffPolicyCorrector offPolicyCorrector,
        PpoEwmaConfig? config = null)
    {
        _snapshotStore = snapshotStore;
        _offPolicyCorrector = offPolicyCorrector;
        _config = config ?? (snapshotStore.CurrentVersion > 0
            ? PpoEwmaConfig.ForStaleWindow(Math.Max(1, snapshotStore.CurrentVersion))
            : PpoEwmaConfig.Default);
    }

    public void UpdateEwmaPreferences(Dictionary<string, double> currentPrefs)
    {
        lock (_ewmaLock)
        {
            var beta = _config.DecayBeta;

            if (_ewmaPreferences.Count == 0)
            {
                foreach (var (key, val) in currentPrefs)
                    _ewmaPreferences[key] = val;
            }
            else
            {
                foreach (var (key, val) in currentPrefs)
                {
                    var prev = _ewmaPreferences.TryGetValue(key, out var p) ? p : val;
                    _ewmaPreferences[key] = beta * prev + (1 - beta) * val;
                }

                foreach (var key in _ewmaPreferences.Keys.ToList())
                {
                    if (!currentPrefs.ContainsKey(key))
                        _ewmaPreferences[key] *= beta;
                }
            }

            _ewmaUpdateCount++;
        }
    }

    public EwmaCorrectionResult ComputeEwmaCorrectedAdvantage(
        string toolName,
        double rawAdvantage,
        Dictionary<string, double> currentPreferences,
        Dictionary<string, double> inferencePreferences,
        int rolloutVersion)
    {
        if (_snapshotStore.HasExactOldLogits(rolloutVersion))
            return ComputeFromExact(toolName, rawAdvantage, currentPreferences,
                inferencePreferences, rolloutVersion);

        return ComputeFromEwma(toolName, rawAdvantage, currentPreferences,
            inferencePreferences);
    }

    private EwmaCorrectionResult ComputeFromExact(
        string toolName, double rawAdvantage,
        Dictionary<string, double> currentPreferences,
        Dictionary<string, double> inferencePreferences,
        int rolloutVersion)
    {
        var result = _offPolicyCorrector.ComputeCorrectedAdvantage(
            toolName, rawAdvantage, currentPreferences,
            inferencePreferences, rolloutVersion);

        return new EwmaCorrectionResult
        {
            CorrectedAdvantage = result.CorrectedAdvantage,
            DiscrepancyRatio = result.DiscrepancyRatio,
            StalenessRatio = result.StalenessRatio,
            IsMasked = result.DiscrepancyMasked,
            IsClipped = result.StalenessClipped,
            TokenIsActive = result.TokenIsActive,
            EwmaProxQuality = 1.0,
            WasReset = false
        };
    }

    private EwmaCorrectionResult ComputeFromEwma(
        string toolName, double rawAdvantage,
        Dictionary<string, double> currentPreferences,
        Dictionary<string, double> inferencePreferences)
    {
        var ewmaPrefs = GetEwmaPreferences();
        var c = _config.DiscrepancyThresholdC;
        var eps = _config.StalenessClipEpsilon;

        var rd = ComputeEwmaDiscrepancyRatio(ewmaPrefs, inferencePreferences, toolName);
        var rs = ComputeEwmaStalenessRatio(currentPreferences, ewmaPrefs, toolName);

        bool masked = rd < 1.0 / c || rd > c;

        bool clipped = false;
        if (rawAdvantage > 0 && rs > 1 + eps) clipped = true;
        if (rawAdvantage < 0 && rs < 1 - eps) clipped = true;

        double active = masked ? 0 : (clipped ? 0.5 : 1.0);

        var correctedRs = clipped
            ? (rawAdvantage > 0 ? Math.Min(rs, 1 + eps) : Math.Max(rs, 1 - eps))
            : rs;

        var correctedAdvantage = rawAdvantage * rd * correctedRs;
        correctedAdvantage = Math.Clamp(correctedAdvantage, -10, 10);

        bool wasReset = UpdateMaskAndMaybeReset(masked);

        return new EwmaCorrectionResult
        {
            CorrectedAdvantage = correctedAdvantage,
            DiscrepancyRatio = rd,
            StalenessRatio = rs,
            IsMasked = masked,
            IsClipped = clipped,
            TokenIsActive = active,
            EwmaProxQuality = _ewmaUpdateCount > 0
                ? 1.0 - Math.Abs(rd - 1.0) * 0.5
                : 1.0,
            WasReset = wasReset
        };
    }

    private bool UpdateMaskAndMaybeReset(bool masked)
    {
        Interlocked.Increment(ref _totalTokens);
        if (!masked) Interlocked.Increment(ref _activeTokens);

        var maskValue = _totalTokens > 0
            ? (double)_activeTokens / _totalTokens
            : 1.0;

        _trainInferMaskValue = maskValue;

        if (_config.EnableAutoReset && maskValue < _config.MaskResetThreshold && _ewmaUpdateCount > 0)
        {
            Interlocked.Increment(ref _resetCount);
            Interlocked.Exchange(ref _totalTokens, 0);
            Interlocked.Exchange(ref _activeTokens, 0);
            _trainInferMaskValue = 1.0;

            lock (_ewmaLock)
            {
                _ewmaPreferences.Clear();
                _ewmaUpdateCount = 0;
            }

            return true;
        }

        return false;
    }

    public Dictionary<string, double> GetEwmaPreferences()
    {
        lock (_ewmaLock)
        {
            return new Dictionary<string, double>(_ewmaPreferences);
        }
    }

    private double ComputeEwmaDiscrepancyRatio(
        Dictionary<string, double> ewmaPrefs,
        Dictionary<string, double> inferencePrefs,
        string toolName)
    {
        var ewmaProb = ewmaPrefs.TryGetValue(toolName, out var ep) ? ep : 1e-6;
        var inferProb = inferencePrefs.TryGetValue(toolName, out var ip) ? ip : 1e-6;

        inferProb = Math.Max(_config.MinimumPreference, inferProb);
        ewmaProb = Math.Max(_config.MinimumPreference, ewmaProb);

        return ewmaProb / inferProb;
    }

    private double ComputeEwmaStalenessRatio(
        Dictionary<string, double> currentPrefs,
        Dictionary<string, double> ewmaPrefs,
        string toolName)
    {
        var curProb = currentPrefs.TryGetValue(toolName, out var cp) ? cp : 1e-6;
        var ewmaProb = ewmaPrefs.TryGetValue(toolName, out var ep) ? ep : 1e-6;

        ewmaProb = Math.Max(_config.MinimumPreference, ewmaProb);
        curProb = Math.Max(_config.MinimumPreference, curProb);

        return curProb / ewmaProb;
    }

    public void ForceResetEwma()
    {
        lock (_ewmaLock)
        {
            _ewmaPreferences.Clear();
            _ewmaUpdateCount = 0;
        }

        Interlocked.Exchange(ref _totalTokens, 0);
        Interlocked.Exchange(ref _activeTokens, 0);
        _trainInferMaskValue = 1.0;
        Interlocked.Increment(ref _resetCount);
    }

    public double GetOptimalDecayBeta(int stableWindow)
    {
        return (double)stableWindow / (stableWindow + 2);
    }

    public void UpdateConfig(PpoEwmaConfig config)
    {
        if (config.DecayBeta > 0 && config.DecayBeta < 1.0) { }
    }

    public Dictionary<string, object> GetStats() => new()
    {
        ["ewma_update_count"] = _ewmaUpdateCount,
        ["train_infer_mask"] = Math.Round(_trainInferMaskValue, 3),
        ["reset_count"] = _resetCount,
        ["ewma_preference_count"] = _ewmaPreferences.Count,
        ["decay_beta"] = _config.DecayBeta,
        ["config"] = new
        {
            beta = _config.DecayBeta,
            reset_threshold = _config.MaskResetThreshold,
            discrepancy_c = _config.DiscrepancyThresholdC,
            clip_epsilon = _config.StalenessClipEpsilon
        }
    };
}
