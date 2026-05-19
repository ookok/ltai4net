using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Cell.Lifecycle;

public sealed class DreamLearner
{
    private static readonly Lazy<DreamLearner> _instance = new(() =>
        new DreamLearner(NullLoggerFactory.Instance.CreateLogger<DreamLearner>()));

    public static DreamLearner Instance => _instance.Value;

    private readonly List<DreamCycle> _cycles = new();
    private readonly object _lock = new();
    private readonly Random _rng = new();
    private readonly ILogger<DreamLearner> _logger;

    private const int MaxCycles = 100;

    public DreamLearner() : this(NullLogger<DreamLearner>.Instance) { }

    public DreamLearner(ILogger<DreamLearner> logger)
    {
        _logger = logger ?? NullLogger<DreamLearner>.Instance;
    }

    public Task<DreamCycle> RunDreamCycleAsync(int availableReflexes, string[] patterns)
    {
        var cycleNumber = 0;
        lock (_lock)
        {
            cycleNumber = _cycles.Count + 1;
        }

        var patternsList = patterns.ToList();
        var discoverCount = _rng.Next(1, Math.Min(4, patternsList.Count + 1));
        var discovered = patternsList.OrderBy(_ => _rng.Next()).Take(discoverCount).ToArray();

        var reflexesImproved = _rng.Next(0, Math.Min(3, availableReflexes + 1));
        var insightsGenerated = _rng.Next(0, 3);

        var dreamDurationMs = 50 + _rng.Next(0, 200);

        var cycle = new DreamCycle
        {
            Cycle = cycleNumber,
            PatternsDiscovered = discovered,
            ReflexesImproved = reflexesImproved,
            DreamDurationMs = dreamDurationMs,
            InsightsGenerated = insightsGenerated
        };

        lock (_lock)
        {
            _cycles.Add(cycle);
            while (_cycles.Count > MaxCycles)
                _cycles.RemoveAt(0);
        }

        _logger.LogInformation("Dream cycle {Cycle}: {Patterns} patterns, {Reflexes} reflexes, {Insights} insights",
            cycleNumber, discovered.Length, reflexesImproved, insightsGenerated);

        return Task.FromResult(cycle);
    }

    public IReadOnlyList<DreamCycle> GetRecentCycles(int count = 10)
    {
        lock (_lock)
        {
            return _cycles.OrderByDescending(c => c.Cycle).Take(count).Reverse().ToList();
        }
    }

    public bool ShouldDream(int cyclesSinceLastDream)
    {
        return cyclesSinceLastDream > 5;
    }

    public Dictionary<string, object> GetStats()
    {
        lock (_lock)
        {
            return new Dictionary<string, object>
            {
                ["total_cycles"] = _cycles.Count,
                ["total_patterns"] = _cycles.Sum(c => c.PatternsDiscovered.Length),
                ["total_insights"] = _cycles.Sum(c => c.InsightsGenerated)
            };
        }
    }
}
