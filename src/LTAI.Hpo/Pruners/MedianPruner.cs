using System;
using System.Collections.Generic;
using System.Linq;

namespace LTAI.Hpo.Pruners;

/// <summary>
/// Prune trials whose intermediate value is worse than the median of
/// completed trials at the same step.
/// </summary>
public sealed class MedianPruner : IPruner
{
    private readonly int _minTrials;

    public MedianPruner(int minTrials = 5)
    {
        _minTrials = minTrials;
    }

    public bool ShouldPrune(Trial trial)
    {
        if (trial.IntermediateValues.Count == 0) return false;

        var last = trial.IntermediateValues[^1];
        var currentValue = last.Value;
        var currentStep = last.Step;

        // We need access to completed trials — this is called from Study,
        // but Trial doesn't hold a reference to completed trials.
        // The pruner is invoked inline, so Study passes context via a closure.
        return false;
    }

    /// <summary>Internal: compare against a set of history values at the same step.</summary>
    internal static bool ShouldPrune(Trial trial, IReadOnlyList<double> valuesAtStep, StudyDirection dir)
    {
        if (trial.IntermediateValues.Count == 0 || valuesAtStep.Count < 5)
            return false;

        var last = trial.IntermediateValues[^1];
        var sorted = valuesAtStep.OrderBy(v => v).ToList();
        var median = sorted[sorted.Count / 2];

        return dir == StudyDirection.Minimize
            ? last.Value > median
            : last.Value < median;
    }
}