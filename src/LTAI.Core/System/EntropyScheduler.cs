namespace LTAI.Core.System;

public enum EntropyScheduleType
{
    Linear,
    Cosine,
    Exponential,
    Constant
}

public sealed class EntropyScheduleConfig
{
    public EntropyScheduleType Type { get; set; } = EntropyScheduleType.Linear;
    public double InitialEntropy { get; set; } = 1.0;
    public double TargetEntropy { get; set; } = 0.25;
    public int WarmupSteps { get; set; } = 100;
    public int TotalSteps { get; set; } = 1000;
    public double MinEntropy { get; set; } = 0.05;
}

public sealed class EntropyScheduler
{
    private readonly EntropyScheduleConfig _config;
    private int _step;
    private double _currentEntropy;

    public double CurrentEntropy => _currentEntropy;
    public int Step => _step;
    public double ExplorationRatio => _currentEntropy / _config.InitialEntropy;

    public EntropyScheduler(EntropyScheduleConfig? config = null)
    {
        _config = config ?? new EntropyScheduleConfig();
        _currentEntropy = _config.InitialEntropy;
    }

    public void StepForward(int steps = 1)
    {
        _step += steps;
        _currentEntropy = ComputeEntropy(_step);
    }

    public double ComputeEntropy(int step)
    {
        if (step <= _config.WarmupSteps)
            return _config.InitialEntropy;

        int effectiveStep = step - _config.WarmupSteps;
        int effectiveTotal = Math.Max(1, _config.TotalSteps - _config.WarmupSteps);
        double progress = Math.Min(1.0, (double)effectiveStep / effectiveTotal);
        double rawEntropy;

        switch (_config.Type)
        {
            case EntropyScheduleType.Cosine:
                rawEntropy = _config.TargetEntropy +
                    (_config.InitialEntropy - _config.TargetEntropy) * (1.0 + Math.Cos(Math.PI * progress)) / 2.0;
                break;

            case EntropyScheduleType.Exponential:
                double gamma = _config.TargetEntropy > 0
                    ? Math.Pow(_config.TargetEntropy / _config.InitialEntropy, 1.0 / effectiveTotal)
                    : 0.98;
                rawEntropy = _config.InitialEntropy * Math.Pow(gamma, effectiveStep);
                break;

            case EntropyScheduleType.Constant:
                rawEntropy = _config.InitialEntropy;
                break;

            case EntropyScheduleType.Linear:
            default:
                rawEntropy = _config.InitialEntropy + (_config.TargetEntropy - _config.InitialEntropy) * progress;
                break;
        }

        return Math.Max(_config.MinEntropy, rawEntropy);
    }

    public bool ShouldExplore(Random? rng = null)
    {
        rng ??= Random.Shared;
        return rng.NextDouble() < _currentEntropy;
    }

    public double GetTemperatureScale()
    {
        return 0.3 + _currentEntropy * 0.7;
    }

    public (double explore, double refine, double exploit) GetActionWeights()
    {
        double e = _currentEntropy;
        double exploit = Math.Max(0.1, 1.0 - e);
        double explore = e * 0.6;
        double refine = 1.0 - exploit - explore;
        return (explore, refine, exploit);
    }

    public void Reset()
    {
        _step = 0;
        _currentEntropy = _config.InitialEntropy;
    }

    public EntropyScheduleConfig Config => _config;
}
