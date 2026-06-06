using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LTAI.Hpo.Pruners;
using LTAI.Hpo.Storage;

namespace LTAI.Hpo;

/// <summary>
/// A hyperparameter optimization study. Coordinates <see cref="Trial"/>s,
/// delegates parameter suggestion to <see cref="ISampler"/>,
/// and persists progress via <see cref="IStudyStore"/>.
/// </summary>
public sealed class Study
{
    private readonly List<TrialRecord> _completedCache = new();
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private int _trialCounter;

    /// <summary>Name of this study (used as storage key).</summary>
    public string Name { get; }

    /// <summary>Optimization direction.</summary>
    public StudyDirection Direction { get; }

    /// <summary>Sampler used to suggest parameters.</summary>
    public ISampler Sampler { get; }

    /// <summary>Optional pruner to stop unpromising trials early.</summary>
    public IPruner? Pruner { get; }

    /// <summary>Persistent store; null = in-memory only.</summary>
    public IStudyStore? Store { get; }

    /// <summary>Best value seen so far.</summary>
    public double? BestValue { get; private set; }

    /// <summary>Parameters that produced <see cref="BestValue"/>.</summary>
    public IReadOnlyDictionary<string, object>? BestParams { get; private set; }

    /// <summary>Total trials completed so far.</summary>
    public int CompletedCount => _completedCache.Count;

    /// <summary>Elapsed time since study start.</summary>
    public TimeSpan Elapsed => _sw.Elapsed;

    /// <summary>Fires whenever a trial completes (for dashboard updates).</summary>
    public event Action<TrialRecord>? OnTrialCompleted;

    public Study(string name,
        ISampler sampler,
        IStudyStore? store = null,
        IPruner? pruner = null,
        StudyDirection direction = StudyDirection.Minimize)
    {
        Name = name;
        Sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        Store = store;
        Pruner = pruner;
        Direction = direction;
    }

    /// <summary>Run optimization.</summary>
    /// <param name="objective">Function that receives a <see cref="Trial"/> and returns a score.</param>
    /// <param name="nTrials">Number of trials to run.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task OptimizeAsync(
        Func<Trial, Task<double>> objective,
        int nTrials,
        CancellationToken ct = default)
    {
        if (Store != null) await Store.InitializeAsync();

        var history = Store?.LoadTrialsAsync(Name).Result;
        if (history != null)
        {
            _completedCache.AddRange(history.Where(t => t.State is TrialState.Completed or TrialState.Pruned));
            _trialCounter = history.Count;
            ReplayBest();
        }

        for (int i = 0; i < nTrials && !ct.IsCancellationRequested; i++)
        {
            var trial = new Trial
            {
                Number = Interlocked.Increment(ref _trialCounter),
                StudyName = Name,
                Direction = Direction,
                Sampler = Sampler,
                Store = Store,
            };

            TrialRecord record;
            try
            {
                var value = await objective(trial).ConfigureAwait(false);

                // Check pruner after objective completes
                if (Pruner != null && trial.IntermediateValues.Count > 0)
                {
                    if (Pruner.ShouldPrune(trial))
                    {
                        record = ToRecord(trial, value, TrialState.Pruned);
                        _completedCache.Add(record);
                        await (Store?.SaveTrialAsync(Name, record) ?? Task.CompletedTask);
                        OnTrialCompleted?.Invoke(record);
                        continue;
                    }
                }

                record = ToRecord(trial, value, TrialState.Completed);
                UpdateBest(record);
            }
            catch (Exception ex)
            {
                record = new TrialRecord
                {
                    Number = trial.Number,
                    State = TrialState.Failed,
                    ErrorMessage = ex.Message,
                    Params = new Dictionary<string, object>(trial.Params),
                    CreatedAt = DateTime.UtcNow,
                };
            }

            _completedCache.Add(record);
            await (Store?.SaveTrialAsync(Name, record) ?? Task.CompletedTask);
            OnTrialCompleted?.Invoke(record);
        }
    }

    /// <summary>Run optimization with a typed result.</summary>
    public async Task<T> OptimizeAsync<T>(
        Func<Trial, Task<(double Score, T Result)>> objective,
        int nTrials,
        CancellationToken ct = default)
    {
        T bestResult = default!;
        await OptimizeAsync(async trial =>
        {
            var (score, result) = await objective(trial).ConfigureAwait(false);
            if (BestValue.HasValue &&
                (Direction == StudyDirection.Minimize && score < BestValue.Value ||
                 Direction == StudyDirection.Maximize && score > BestValue.Value))
            {
                bestResult = result;
            }
            else if (!BestValue.HasValue)
            {
                bestResult = result;
            }
            return score;
        }, nTrials, ct).ConfigureAwait(false);
        return bestResult;
    }

    // ── internal ──

    private static TrialRecord ToRecord(Trial trial, double value, TrialState state) => new()
    {
        Number = trial.Number,
        State = state,
        Value = value,
        Params = new Dictionary<string, object>(trial.Params),
        IntermediateValues = trial.IntermediateValues.ToList(),
        CreatedAt = DateTime.UtcNow,
    };

    private void UpdateBest(TrialRecord record)
    {
        if (!record.Value.HasValue) return;
        var v = record.Value.Value;
        if (!BestValue.HasValue ||
            (Direction == StudyDirection.Minimize && v < BestValue.Value) ||
            (Direction == StudyDirection.Maximize && v > BestValue.Value))
        {
            BestValue = v;
            BestParams = record.Params;
        }
    }

    private void ReplayBest()
    {
        foreach (var r in _completedCache)
            UpdateBest(r);
    }
}