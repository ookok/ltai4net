using System.Diagnostics;

namespace LTAI.Core.System;

public sealed record AllocationStage
{
    public string Stage { get; init; } = "";
    public double AllocatedMs { get; init; }
    public double ActualMs { get; set; }
    public double RemainingMs { get; set; }
    public bool Exceeded => ActualMs > AllocatedMs;
}

public sealed record AllocationReport
{
    public double TotalBudgetMs { get; init; }
    public double TotalElapsedMs { get; init; }
    public double UnusedMs { get; init; }
    public List<AllocationStage> Stages { get; init; } = new();
    public bool AnyExceeded => Stages.Any(s => s.Exceeded);
}

public sealed class LatencyBudgetAllocator
{
    private static readonly (string Stage, double Fraction)[] DefaultStages =
    [
        ("Retrieval", 0.30),
        ("PromptBuild", 0.05),
        ("LLM_Call", 0.50),
        ("PostProcess", 0.15)
    ];

    private readonly double _totalBudgetMs;
    private readonly Dictionary<string, double> _allocations;
    private readonly Dictionary<string, Stopwatch> _watches;
    private readonly Dictionary<string, double> _actuals;

    public LatencyBudgetAllocator(double totalLatencyBudgetMs,
        (string Stage, double Fraction)[]? customStages = null)
    {
        _totalBudgetMs = totalLatencyBudgetMs;
        var stages = customStages ?? DefaultStages;
        _allocations = new Dictionary<string, double>();
        _watches = new Dictionary<string, Stopwatch>();
        _actuals = new Dictionary<string, double>();

        foreach (var (stage, fraction) in stages)
        {
            _allocations[stage] = totalLatencyBudgetMs * fraction;
            _watches[stage] = new Stopwatch();
            _actuals[stage] = 0;
        }
    }

    public void StartStage(string stage)
    {
        if (_watches.TryGetValue(stage, out var sw))
            sw.Start();
    }

    public void EndStage(string stage)
    {
        if (!_watches.TryGetValue(stage, out var sw) || !sw.IsRunning)
            return;

        sw.Stop();
        _actuals[stage] = sw.Elapsed.TotalMilliseconds;

        var overBudget = _actuals[stage] - _allocations[stage];
        if (overBudget > 0)
            RedistributeAfterOverage(stage, overBudget);
    }

    public double RemainingForStage(string stage)
    {
        return _allocations.TryGetValue(stage, out var alloc) ? alloc : 0;
    }

    public AllocationReport GenerateReport()
    {
        double totalElapsed = 0;
        var stages = new List<AllocationStage>();

        foreach (var (stage, alloc) in _allocations)
        {
            var actual = _actuals.GetValueOrDefault(stage, 0);
            totalElapsed += actual;

            var stageKey = new HashSet<string>(_allocations.Keys).OrderBy(k =>
                Array.IndexOf(DefaultStages.Select(s => s.Stage).ToArray(), k)).ToList();
            var idx = stageKey.IndexOf(stage);
            var subsequentBudget = _allocations
                .Where(kv => stageKey.IndexOf(kv.Key) > idx)
                .Sum(kv => kv.Value);

            stages.Add(new AllocationStage
            {
                Stage = stage,
                AllocatedMs = Math.Round(alloc, 1),
                ActualMs = Math.Round(actual, 1),
                RemainingMs = Math.Round(subsequentBudget, 1)
            });
        }

        return new AllocationReport
        {
            TotalBudgetMs = Math.Round(_totalBudgetMs, 1),
            TotalElapsedMs = Math.Round(totalElapsed, 1),
            UnusedMs = Math.Round(Math.Max(0, _totalBudgetMs - totalElapsed), 1),
            Stages = stages
        };
    }

    private void RedistributeAfterOverage(string exceededStage, double overageMs)
    {
        var stageOrder = _allocations.Keys.ToList();
        var exceededIdx = stageOrder.IndexOf(exceededStage);
        if (exceededIdx < 0 || exceededIdx >= stageOrder.Count - 1)
            return;

        var subsequentStages = stageOrder.Skip(exceededIdx + 1).ToList();
        if (subsequentStages.Count == 0)
            return;

        var overagePerStage = overageMs / subsequentStages.Count;

        for (var i = exceededIdx + 1; i < stageOrder.Count; i++)
        {
            var stage = stageOrder[i];
            _allocations[stage] = Math.Max(0, _allocations[stage] - overagePerStage);
        }
    }
}
