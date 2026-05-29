using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

// ============================================================================
// MAP-Elites inspired Island Sampler (ASI-Evolve)
// Quality-Diversity: maintains islands of diverse high-performing candidates.
// Integrates with MoE domain experts as natural islands.
// ============================================================================

public sealed record IslandCell
{
    public string IslandId { get; init; } = "";
    public string Domain { get; init; } = "general";
    public string CandidateId { get; init; } = "";
    public float Fitness { get; init; }
    public float DiversityScore { get; init; }
    public int Generation { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public int SelectionCount { get; set; }
}

public sealed record IslandSelection
{
    public string IslandId { get; init; } = "";
    public string CandidateId { get; init; } = "";
    public float Fitness { get; init; }
    public float SelectionScore { get; init; }
    public string Algorithm { get; init; } = "";
}

public enum IslandSamplingAlgorithm
{
    UCB1,
    Greedy,
    Random,
    IslandElite
}

public sealed class IslandSampler
{
    private readonly ILogger<IslandSampler> _logger;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, IslandCell>> _islands = new();
    private readonly ConcurrentDictionary<string, int> _selectionCounts = new();
    private readonly ConcurrentDictionary<string, int> _globalSelectionCounts = new();
    private readonly ConcurrentDictionary<string, List<double>> _islandFitnessHistory = new();
    private readonly int _gridSize;
    private int _totalSelections;
    private const int MaxIslands = 6;

    public IslandSampler(int gridSize = 6, ILogger<IslandSampler>? logger = null)
    {
        _gridSize = gridSize;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<IslandSampler>.Instance;

        var domains = new[] { "code", "math", "chat", "reasoning", "eia", "general" };
        foreach (var d in domains)
        {
            _islands[d] = new ConcurrentDictionary<string, IslandCell>();
            _globalSelectionCounts[d] = 0;
        }
    }

    // ========================================================================
    // 1. Register a candidate into a specific island (domain)
    // ========================================================================

    public IslandCell RegisterCandidate(string domain, string candidateId, float fitness, float diversity = 0.5f)
    {
        var island = _islands.GetOrAdd(domain, _ => new ConcurrentDictionary<string, IslandCell>());

        var cell = new IslandCell
        {
            IslandId = domain,
            Domain = domain,
            CandidateId = candidateId,
            Fitness = fitness,
            DiversityScore = diversity,
            Generation = island.Count / _gridSize
        };

        island[candidateId] = cell;

        var history = _islandFitnessHistory.GetOrAdd(domain, _ => new List<double>());
        history.Add(fitness);
        if (history.Count > 500) history.RemoveAt(0);

        var maxPerIsland = _gridSize * 4;
        if (island.Count > maxPerIsland)
        {
            var worst = island.Values.OrderBy(c => c.Fitness * 0.6f + c.DiversityScore * 0.4f).First();
            island.TryRemove(worst.CandidateId, out _);
        }

        return cell;
    }

    // ========================================================================
    // 2. Select a candidate using configured algorithm
    // ========================================================================

    public IslandSelection Select(string domain, IslandSamplingAlgorithm algorithm = IslandSamplingAlgorithm.UCB1)
    {
        var island = _islands.GetOrAdd(domain, _ => new ConcurrentDictionary<string, IslandCell>());
        if (island.Count == 0)
            return new IslandSelection { IslandId = domain, Algorithm = algorithm.ToString() };

        _globalSelectionCounts.AddOrUpdate(domain, 1, (_, c) => c + 1);
        Interlocked.Increment(ref _totalSelections);

        var result = algorithm switch
        {
            IslandSamplingAlgorithm.UCB1 => SelectUCB1(island, domain),
            IslandSamplingAlgorithm.Greedy => SelectGreedy(island),
            IslandSamplingAlgorithm.Random => SelectRandom(island),
            IslandSamplingAlgorithm.IslandElite => SelectIslandElite(island, domain),
            _ => SelectUCB1(island, domain)
        };

        var selection = result with { Algorithm = algorithm.ToString() };
        _selectionCounts.AddOrUpdate(selection.CandidateId, 1, (_, c) => c + 1);
        return selection;
    }

    // ========================================================================
    // 3. MAP-Elites Elite sampling: maintain quality + diversity
    // ========================================================================

    public List<IslandCell> CreateEliteGrid(string primaryDomain, int gridSize = 6)
    {
        var cells = new List<IslandCell>();

        var allCandidates = _islands
            .Where(kv => kv.Key == primaryDomain || kv.Key == "general" || ComputeDomainSimilarity(primaryDomain, kv.Key) > 0.5)
            .SelectMany(kv => kv.Value.Values)
            .ToList();

        if (allCandidates.Count == 0) return cells;

        var sorted = allCandidates.OrderByDescending(c => c.Fitness).ToList();

        var diversityBuckets = new List<List<IslandCell>>();
        for (int i = 0; i < gridSize; i++)
            diversityBuckets.Add(new List<IslandCell>());

        foreach (var cell in sorted)
        {
            var bucketIdx = (int)(cell.DiversityScore * gridSize);
            bucketIdx = Math.Clamp(bucketIdx, 0, gridSize - 1);
            if (diversityBuckets[bucketIdx].Count < 2)
                diversityBuckets[bucketIdx].Add(cell);
        }

        foreach (var bucket in diversityBuckets)
        foreach (var cell in bucket.Take(1))
            cells.Add(cell);

        while (cells.Count < Math.Min(gridSize, sorted.Count))
        {
            var best = sorted.FirstOrDefault(c => !cells.Any(existing => existing.CandidateId == c.CandidateId));
            if (best == null) break;
            cells.Add(best);
        }

        _logger.LogDebug("Elite grid: domain={Domain} cells={Cells} totalCandidates={Total}",
            primaryDomain, cells.Count, sorted.Count);

        return cells;
    }

    // ========================================================================
    // 4. Cross-island migration: transfer successful patterns
    // ========================================================================

    public List<string> MigrateCandidates(string sourceDomain, string targetDomain, float fitnessThreshold = 0.6f)
    {
        var sourceIsland = _islands.GetOrAdd(sourceDomain, _ => new());
        var migrated = new List<string>();

        var candidates = sourceIsland.Values
            .Where(c => c.Fitness >= fitnessThreshold)
            .OrderByDescending(c => c.Fitness)
            .Take(3)
            .ToList();

        foreach (var candidate in candidates)
        {
            RegisterCandidate(targetDomain, $"{candidate.CandidateId}_migrated_from_{sourceDomain}",
                candidate.Fitness * 0.85f, candidate.DiversityScore);
            migrated.Add(candidate.CandidateId);
        }

        if (migrated.Count > 0)
            _logger.LogInformation("Island migration: {Count} candidates from {Source} → {Target}",
                migrated.Count, sourceDomain, targetDomain);

        return migrated;
    }

    // ========================================================================
    // 5. Stats
    // ========================================================================

    public Dictionary<string, object> GetStats()
    {
        var islandStats = new Dictionary<string, object>();
        foreach (var (domain, island) in _islands)
        {
            islandStats[domain] = new
            {
                count = island.Count,
                avgFitness = island.Count > 0 ? island.Values.Average(c => c.Fitness) : 0,
                maxFitness = island.Count > 0 ? island.Values.Max(c => c.Fitness) : 0,
                avgDiversity = island.Count > 0 ? island.Values.Average(c => c.DiversityScore) : 0,
                selections = _globalSelectionCounts.GetValueOrDefault(domain)
            };
        }

        return new Dictionary<string, object>
        {
            ["total_selections"] = _totalSelections,
            ["island_count"] = _islands.Count,
            ["islands"] = islandStats
        };
    }

    public IslandCell? GetBestCandidate(string domain)
    {
        var island = _islands.GetOrAdd(domain, _ => new());
        return island.Values.MaxBy(c => c.Fitness);
    }

    // ========================================================================
    // Private selection algorithms
    // ========================================================================

    private IslandSelection SelectUCB1(ConcurrentDictionary<string, IslandCell> island, string domain)
    {
        var globalCount = _globalSelectionCounts.GetValueOrDefault(domain, 1);

        var best = island.Values
            .Select(c =>
            {
                var exploitation = c.Fitness;
                var exploration = _selectionCounts.GetValueOrDefault(c.CandidateId, 0) > 0
                    ? Math.Sqrt(2 * Math.Log(globalCount + 1) / _selectionCounts.GetValueOrDefault(c.CandidateId, 1))
                    : 10.0;
                return (cell: c, score: exploitation + exploration);
            })
            .MaxBy(x => x.score);

        return new IslandSelection
        {
            IslandId = best.cell.IslandId,
            CandidateId = best.cell.CandidateId,
            Fitness = best.cell.Fitness,
            SelectionScore = (float)best.score
        };
    }

    private static IslandSelection SelectGreedy(ConcurrentDictionary<string, IslandCell> island)
    {
        var best = island.Values.MaxBy(c => c.Fitness)
            ?? new IslandCell();

        return new IslandSelection
        {
            IslandId = best.IslandId,
            CandidateId = best.CandidateId,
            Fitness = best.Fitness,
            SelectionScore = best.Fitness
        };
    }

    private static IslandSelection SelectRandom(ConcurrentDictionary<string, IslandCell> island)
    {
        var candidates = island.Values.ToArray();
        var selected = candidates[Random.Shared.Next(candidates.Length)];

        return new IslandSelection
        {
            IslandId = selected.IslandId,
            CandidateId = selected.CandidateId,
            Fitness = selected.Fitness,
            SelectionScore = selected.Fitness
        };
    }

    private IslandSelection SelectIslandElite(ConcurrentDictionary<string, IslandCell> island, string domain)
    {
        var fitnessHistory = _islandFitnessHistory.GetOrAdd(domain, _ => new List<double>());
        var avgFitness = fitnessHistory.Count > 0 ? fitnessHistory.Average() : 0.5;
        var candidates = island.Values.ToArray();

        if (candidates.Length == 0)
            return new IslandSelection { IslandId = domain };

        var scored = candidates
            .Select(c =>
            {
                var quality = c.Fitness;
                var diversity = c.DiversityScore;
                var novelty = Math.Abs(c.Fitness - avgFitness);
                var score = quality * 0.5 + diversity * 0.3 + novelty * 0.2;
                return (cell: c, score);
            })
            .MaxBy(x => x.score);

        return new IslandSelection
        {
            IslandId = scored.cell.IslandId,
            CandidateId = scored.cell.CandidateId,
            Fitness = scored.cell.Fitness,
            SelectionScore = (float)scored.score
        };
    }

    private static double ComputeDomainSimilarity(string a, string b)
    {
        if (a == b) return 1.0;

        var pairs = new Dictionary<string, double>
        {
            ["code_reasoning"] = 0.7, ["code_math"] = 0.5,
            ["reasoning_eia"] = 0.6, ["chat_general"] = 0.8,
            ["math_reasoning"] = 0.7
        };

        var key1 = $"{a}_{b}";
        var key2 = $"{b}_{a}";
        return pairs.GetValueOrDefault(key1, 0) > 0 ? pairs[key1]
            : pairs.GetValueOrDefault(key2, 0) > 0 ? pairs[key2] : 0.1;
    }
}
