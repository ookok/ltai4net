using System;
using System.Collections.Generic;
using System.Linq;

namespace LTAI.Hpo.Samplers;

/// <summary>
/// Grid sampler — enumerates all combinations for small search spaces.
/// Use only when total combinations ≤ 500.
/// </summary>
public sealed class GridSampler : ISampler
{
    private readonly Dictionary<string, object[]> _grid;
    private int _index;

    /// <param name="grid">Parameter name → list of discrete values to try.</param>
    public GridSampler(Dictionary<string, object[]> grid)
    {
        _grid = grid ?? throw new ArgumentNullException(nameof(grid));
    }

    public float SampleFloat(Trial trial, string name, float low, float high, bool log)
    {
        if (_grid.TryGetValue(name, out var values) && values.Length > 0)
            return Convert.ToSingle(values[_index % values.Length]);
        return low;
    }

    public int SampleInt(Trial trial, string name, int low, int high)
    {
        if (_grid.TryGetValue(name, out var values) && values.Length > 0)
            return Convert.ToInt32(values[_index % values.Length]);
        return low;
    }

    public T SampleCategorical<T>(Trial trial, string name, T[] choices) where T : notnull
    {
        if (_grid.TryGetValue(name, out var values) && values.Length > 0)
            return (T)values[_index % values.Length];
        return choices[0];
    }

    internal int AdvanceIndex(int? forced = null)
    {
        if (forced.HasValue) _index = forced.Value;
        else _index++;
        return _index;
    }
}
