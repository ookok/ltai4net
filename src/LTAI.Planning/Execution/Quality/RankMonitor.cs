using LTAI.Planning.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Planning.Quality;

public sealed class RankMonitor
{
    private const int HistoryWindow = 10;
    private const int EntropyBins = 10;
    private const double Epsilon = 1e-10;

    private readonly List<RankSnapshot> _history = new(HistoryWindow);
    private readonly Lock _historyLock = new();
    private ILogger _logger;

    private static readonly Lazy<RankMonitor> _instance = new(() => new RankMonitor());
    public static RankMonitor Instance => _instance.Value;

    private RankMonitor()
    {
        _logger = NullLoggerFactory.Instance.CreateLogger<RankMonitor>();
    }

    public void SetLogger(ILogger logger)
    {
        _logger = logger;
    }

    public RankSnapshot Analyze<T>(List<T> population, Func<T, double> fitnessSelector)
    {
        if (population == null || population.Count == 0)
        {
            var empty = new RankSnapshot
            {
                Timestamp = DateTime.UtcNow,
                PopulationSize = 0,
                EffectiveRank = 0,
                DiversityScore = 0,
                DominantDirectionCount = 0,
                Entropy = 0,
                State = DiversityState.Frozen
            };
            AppendHistory(empty);
            return empty;
        }

        var fitness = population.Select(fitnessSelector).ToList();
        int popSize = fitness.Count;

        var uniqueRounded = fitness.Select(f => Math.Round(f, 2)).Distinct().Count();

        double mean = fitness.Average();
        double stddev = Math.Sqrt(fitness.Sum(f => (f - mean) * (f - mean)) / popSize);
        double diversityScore = mean != 0 ? stddev / mean : 0;

        double minFit = fitness.Min();
        double maxFit = fitness.Max();
        double binWidth = maxFit > minFit ? (maxFit - minFit) / EntropyBins : 1.0;
        int[] bins = new int[EntropyBins];
        foreach (double f in fitness)
        {
            int idx = binWidth > 0 ? (int)Math.Min((f - minFit) / binWidth, EntropyBins - 1) : 0;
            bins[idx]++;
        }
        double entropy = 0;
        foreach (int count in bins)
        {
            if (count == 0) continue;
            double p = (double)count / popSize;
            entropy -= p * Math.Log2(p + Epsilon);
        }
        double normalizedEntropy = entropy / Math.Log2(EntropyBins);

        double topFitness = fitness.Max();
        double threshold = topFitness * 0.9;
        int dominantCount = fitness.Count(f => f >= threshold);

        var state = ClassifyState(uniqueRounded, diversityScore, popSize);

        var snapshot = new RankSnapshot
        {
            Timestamp = DateTime.UtcNow,
            PopulationSize = popSize,
            EffectiveRank = uniqueRounded,
            DiversityScore = diversityScore,
            DominantDirectionCount = dominantCount,
            Entropy = normalizedEntropy,
            State = state
        };

        AppendHistory(snapshot);
        return snapshot;
    }

    public bool ShouldIntervene()
    {
        RankSnapshot? latest = GetLatest();
        if (latest == null) return false;
        return latest.State is DiversityState.Condensing
            or DiversityState.Collapsing
            or DiversityState.Frozen;
    }

    public double GetInterventionStrength()
    {
        RankSnapshot? latest = GetLatest();
        if (latest == null) return 0.0;
        return latest.State switch
        {
            DiversityState.Healthy => 0.0,
            DiversityState.Condensing => 0.2,
            DiversityState.Collapsing => 0.5,
            DiversityState.Frozen => 0.8,
            _ => 0.0
        };
    }

    public DiversityState ClassifyState(int effectiveRank, double diversityScore, int populationSize)
    {
        if (diversityScore < 0.1 || effectiveRank <= 1)
            return DiversityState.Frozen;
        if (diversityScore < 0.25 || effectiveRank <= 2)
            return DiversityState.Collapsing;
        if (diversityScore < 0.4 || effectiveRank <= (double)populationSize / 4)
            return DiversityState.Condensing;
        return DiversityState.Healthy;
    }

    public Dictionary<string, object?> GetStats()
    {
        RankSnapshot? latest = GetLatest();
        lock (_historyLock)
        {
            return new Dictionary<string, object?>
            {
                ["HistorySize"] = _history.Count,
                ["CurrentState"] = latest?.State.ToString() ?? "Unknown",
                ["LatestDiversityScore"] = latest?.DiversityScore ?? 0.0
            };
        }
    }

    private void AppendHistory(RankSnapshot snapshot)
    {
        lock (_historyLock)
        {
            _history.Add(snapshot);
            while (_history.Count > HistoryWindow)
                _history.RemoveAt(0);
        }
    }

    private RankSnapshot? GetLatest()
    {
        lock (_historyLock)
        {
            return _history.Count > 0 ? _history[^1] : null;
        }
    }
}
