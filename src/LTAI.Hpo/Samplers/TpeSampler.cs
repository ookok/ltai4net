using System;
using System.Collections.Generic;
using System.Linq;

namespace LTAI.Hpo.Samplers;

/// <summary>
/// Tree-structured Parzen Estimator (TPE) sampler.
/// Models good vs. bad density with 1D Gaussian Parzen windows per parameter.
/// </summary>
/// <remarks>
/// Implementation based on:
///   Bergstra et al. "Algorithms for Hyper-Parameter Optimization" (NeurIPS 2011)
/// Simplified: independent 1D KDE per parameter using a Gaussian kernel.
///   Selection ratio γ = 0.25 (top 25% = "good", rest = "bad").
/// </remarks>
public sealed class TpeSampler : ISampler
{
    private readonly int _seed;
    private readonly Random _rng;
    private readonly double _gamma;

    public TpeSampler(int? seed = null, double gamma = 0.25)
    {
        _seed = seed ?? Environment.TickCount;
        _rng = new Random(_seed);
        _gamma = gamma;
    }

    public float SampleFloat(Trial trial, string name, float low, float high, bool log)
    {
        var completed = trial.Store?.LoadTrialsAsync(trial.StudyName).Result
            .Where(t => t.State is TrialState.Completed or TrialState.Pruned).ToList();

        if (completed == null || completed.Count < 3)
        {
            if (log)
            {
                var logLow = Math.Log(low);
                var logHigh = Math.Log(high);
                return (float)Math.Exp(logLow + _rng.NextDouble() * (logHigh - logLow));
            }
            return (float)(low + _rng.NextDouble() * (high - low));
        }

        // Collect values for this parameter from completed trials
        var pairs = new List<(double Param, double Score)>();
        var dir = trial.Direction;
        foreach (var rec in completed)
        {
            if (rec.Params.TryGetValue(name, out var raw) && rec.Value.HasValue)
            {
                pairs.Add((Convert.ToDouble(raw), rec.Value.Value));
            }
        }

        if (pairs.Count < 3)
            goto fallback;

        // Sort by score (direction-aware)
        pairs.Sort(dir == StudyDirection.Minimize
            ? (a, b) => a.Score.CompareTo(b.Score)
            : (a, b) => b.Score.CompareTo(a.Score));

        var nGood = Math.Max(2, (int)(pairs.Count * _gamma));
        var good = pairs.Take(nGood).Select(p => p.Param).ToList();
        var bad = pairs.Skip(nGood).Select(p => p.Param).ToList();

        // Sample from good KDE, evaluate EI = l(x) / g(x) at each candidate
        var candidates = SampleCandidates(good, low, high, log, 24);
        double bestEi = double.NegativeInfinity;
        float bestCandidate = 0;

        foreach (var c in candidates)
        {
            var l = KdeDensity(c, good, log);
            var g = KdeDensity(c, bad, log);
            var ei = g > 1e-12 ? l / g : double.PositiveInfinity;
            if (ei > bestEi || bestCandidate == 0)
            {
                bestEi = ei;
                bestCandidate = c;
            }
        }

        return bestCandidate;

    fallback:
        // Fall back to random
        if (log)
        {
            var logLow = Math.Log(low);
            var logHigh = Math.Log(high);
            return (float)Math.Exp(logLow + _rng.NextDouble() * (logHigh - logLow));
        }
        return (float)(low + _rng.NextDouble() * (high - low));
    }

    public int SampleInt(Trial trial, string name, int low, int high)
    {
        // Treat as float then round
        var f = SampleFloat(trial, name, low, high + 0.999f, log: false);
        return Math.Clamp((int)Math.Round(f), low, high);
    }

    public T SampleCategorical<T>(Trial trial, string name, T[] choices) where T : notnull
    {
        var completed = trial.Store?.LoadTrialsAsync(trial.StudyName).Result
            .Where(t => t.State is TrialState.Completed or TrialState.Pruned).ToList();

        if (completed == null || completed.Count < 3)
            return choices[_rng.Next(choices.Length)];

        var counts = new Dictionary<T, (int Good, int Bad)>();
        foreach (var c in choices) counts[c] = (0, 0);

        var dir = trial.Direction;
        var scored = completed
            .Where(r => r.Params.TryGetValue(name, out _) && r.Value.HasValue)
            .Select(r => (Choice: (T)r.Params[name], Score: r.Value!.Value))
            .ToList();

        if (scored.Count < 3)
            return choices[_rng.Next(choices.Length)];

        scored.Sort(dir == StudyDirection.Minimize
            ? (a, b) => a.Score.CompareTo(b.Score)
            : (a, b) => b.Score.CompareTo(a.Score));

        var nGood = Math.Max(1, (int)(scored.Count * _gamma));
        for (int i = 0; i < scored.Count; i++)
        {
            var (choice, _) = scored[i];
            if (i < nGood)
                counts[choice] = (counts[choice].Good + 1, counts[choice].Bad);
            else
                counts[choice] = (counts[choice].Good, counts[choice].Bad + 1);
        }

        var weights = new double[choices.Length];
        for (int i = 0; i < choices.Length; i++)
        {
            var (g, b) = counts[choices[i]];
            var l = (g + 1.0) / (nGood + choices.Length);
            var gDen = (b + 1.0) / (scored.Count - nGood + choices.Length);
            weights[i] = gDen > 1e-12 ? l / gDen : 1.0;
        }

        return WeightedSample(choices, weights);
    }

    // ── helpers ──

    private List<float> SampleCandidates(List<double> goodSamples, float low, float high, bool log, int count)
    {
        var candidates = new List<float>();
        // Always include some random candidates for exploration
        for (int i = 0; i < count; i++)
        {
            if (i < goodSamples.Count && i < count / 2)
            {
                // Sample near good points (with Gaussian noise)
                var mu = goodSamples[i];
                var sigma = EstimateBandwidth(goodSamples);
                var x = mu + _rng.NextGaussian() * sigma;
                if (log) x = Math.Min(high, Math.Max(low, (float)Math.Exp(x)));
                else x = Math.Clamp(x, low, high);
                candidates.Add((float)x);
            }
            else
            {
                if (log)
                {
                    var logLow = Math.Log(low);
                    var logHigh = Math.Log(high);
                    candidates.Add((float)Math.Exp(logLow + _rng.NextDouble() * (logHigh - logLow)));
                }
                else
                    candidates.Add((float)(low + _rng.NextDouble() * (high - low)));
            }
        }
        return candidates;
    }

    private static double KdeDensity(double x, List<double> samples, bool log)
    {
        if (samples.Count == 0) return 0;
        var h = EstimateBandwidth(samples);
        if (h < 1e-12) h = 1.0;
        double density = 0;
        foreach (var xi in samples)
        {
            var diff = (x - xi) / h;
            density += (1.0 / Math.Sqrt(2 * Math.PI)) * Math.Exp(-0.5 * diff * diff);
        }
        return density / (samples.Count * h);
    }

    private static double EstimateBandwidth(List<double> samples)
    {
        if (samples.Count < 2) return 1.0;
        var mean = samples.Average();
        var variance = samples.Sum(s => (s - mean) * (s - mean)) / (samples.Count - 1);
        var std = Math.Sqrt(variance);
        // Silverman's rule of thumb
        return std * Math.Pow(4.0 / (3.0 * samples.Count), 0.2);
    }

    private T WeightedSample<T>(T[] choices, double[] weights)
    {
        var total = weights.Sum();
        if (total < 1e-12) return choices[_rng.Next(choices.Length)];
        var r = _rng.NextDouble() * total;
        double cum = 0;
        for (int i = 0; i < choices.Length; i++)
        {
            cum += weights[i];
            if (r <= cum) return choices[i];
        }
        return choices[^1];
    }
}

internal static class RandomExtensions
{
    /// <summary>Box-Muller transform for Gaussian sampling.</summary>
    public static double NextGaussian(this Random rng, double mean = 0, double stddev = 1)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return mean + stddev * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}
