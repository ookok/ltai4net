using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LTAI.Hpo.Dashboard;

/// <summary>
/// In-memory dashboard data for HPO progress.
/// Consumed by TUI/Desktop/Web DevUI panels.
/// </summary>
public sealed class HpoDashboard
{
    private readonly Dictionary<string, Study> _studies = new();

    /// <summary>Register a study for dashboard tracking.</summary>
    public void Track(string name, Study study)
    {
        _studies[name] = study;
        study.OnTrialCompleted += _ => NotifyUpdated();
    }

    /// <summary>All tracked studies.</summary>
    public IReadOnlyDictionary<string, Study> Studies => _studies;

    /// <summary>Get the latest trial records for a study.</summary>
    public IReadOnlyList<TrialRecord> GetRecentTrials(string studyName, int count = 50)
    {
        if (!_studies.TryGetValue(studyName, out var study)) return Array.Empty<TrialRecord>();
        if (study.Store == null) return new List<TrialRecord>();
        var all = study.Store.LoadTrialsAsync(studyName).Result;
        return all.OrderByDescending(t => t.CreatedAt).Take(count).ToList();
    }

    /// <summary>Fires when any tracked study completes a trial.</summary>
    public event Action? OnUpdated;

    private void NotifyUpdated() => OnUpdated?.Invoke();

    /// <summary>Format a trial's parameters as a readable string.</summary>
    public static string FormatParams(IReadOnlyDictionary<string, object>? parameters)
    {
        if (parameters == null || parameters.Count == 0) return "—";
        return string.Join(", ", parameters.Select(kv => $"{kv.Key}={kv.Value}"));
    }
}