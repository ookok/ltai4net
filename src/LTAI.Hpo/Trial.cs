using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LTAI.Hpo.Storage;

namespace LTAI.Hpo;

/// <summary>
/// A single trial within a study. The <c>objective</c> function receives this
/// and calls <see cref="SuggestFloat"/>/<see cref="SuggestInt"/>/<see cref="SuggestCategorical"/>
/// to declare search space, then <see cref="Report"/> to provide intermediate values.
/// </summary>
public sealed class Trial
{
    private readonly Dictionary<string, object> _params = new();
    private readonly List<TrialValue> _values = new();

    /// <summary>Trial number (1-based, unique within a study).</summary>
    public int Number { get; internal set; }
    internal string StudyName { get; set; } = "";
    internal StudyDirection Direction { get; set; }
    internal ISampler Sampler { get; set; } = null!;
    internal IStudyStore? Store { get; set; }

    /// <summary>All suggested parameter values for this trial.</summary>
    public IReadOnlyDictionary<string, object> Params => _params;

    /// <summary>Intermediate values reported via <see cref="Report"/>.</summary>
    public IReadOnlyList<TrialValue> IntermediateValues => _values;

    /// <summary>Best (lowest or highest) intermediate value so far.</summary>
    public double? BestValue => _values.Count > 0
        ? Direction == StudyDirection.Minimize
            ? _values.Min(v => v.Value)
            : _values.Max(v => v.Value)
        : null;

    /// <summary>Suggest a continuous float parameter.</summary>
    public float SuggestFloat(string name, float low, float high, bool log = false)
    {
        if (_params.TryGetValue(name, out var existing))
            return Convert.ToSingle(existing);
        var val = Sampler.SampleFloat(this, name, low, high, log);
        _params[name] = val;
        return val;
    }

    /// <summary>Suggest an integer parameter (inclusive range).</summary>
    public int SuggestInt(string name, int low, int high)
    {
        if (_params.TryGetValue(name, out var existing))
            return Convert.ToInt32(existing);
        var val = Sampler.SampleInt(this, name, low, high);
        _params[name] = val;
        return val;
    }

    /// <summary>Suggest a categorical parameter.</summary>
    public T SuggestCategorical<T>(string name, T[] choices) where T : notnull
    {
        if (_params.TryGetValue(name, out var existing))
            return (T)existing;
        var val = Sampler.SampleCategorical(this, name, choices);
        _params[name] = val!;
        return val;
    }

    /// <summary>Report an intermediate value (for pruning).</summary>
    /// <param name="value">Objective value at this step.</param>
    /// <param name="step">Step number (0-based).</param>
    public void Report(double value, int step)
    {
        _values.Add(new TrialValue(value, step));
    }
}
