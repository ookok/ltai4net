namespace LTAI.Hpo.Pruners;

/// <summary>
/// Prune trials that fail to reach a threshold by the first report step.
/// </summary>
public sealed class ThresholdPruner : IPruner
{
    private readonly double _threshold;
    private readonly int _upper;

    /// <param name="threshold">Value must be ≤ this (minimize) or ≥ this (maximize).</param>
    /// <param name="upper">True if lower bound, false if upper bound.</param>
    public ThresholdPruner(double threshold, bool upper = false)
    {
        _threshold = threshold;
        _upper = upper ? 1 : -1;
    }

    public bool ShouldPrune(Trial trial)
    {
        if (trial.IntermediateValues.Count == 0) return false;
        var last = trial.IntermediateValues[^1].Value;
        return _upper >= 0 ? last < _threshold : last > _threshold;
    }
}