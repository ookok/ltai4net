using System;
using System.Collections.Generic;
using System.Linq;

namespace LTAI.Hpo.Samplers;

/// <summary>
/// Random sampler — uniformly random within bounds.
/// Baseline for TPE comparison.
/// </summary>
public sealed class RandomSampler : ISampler
{
    private readonly Random _rng;

    public RandomSampler(int? seed = null)
    {
        _rng = seed.HasValue ? new Random(seed.Value) : new Random();
    }

    public float SampleFloat(Trial trial, string name, float low, float high, bool log)
    {
        if (log)
        {
            var logLow = Math.Log(low);
            var logHigh = Math.Log(high);
            return (float)Math.Exp(logLow + _rng.NextDouble() * (logHigh - logLow));
        }
        return (float)(low + _rng.NextDouble() * (high - low));
    }

    public int SampleInt(Trial trial, string name, int low, int high)
        => _rng.Next(low, high + 1);

    public T SampleCategorical<T>(Trial trial, string name, T[] choices) where T : notnull
        => choices[_rng.Next(choices.Length)];
}
