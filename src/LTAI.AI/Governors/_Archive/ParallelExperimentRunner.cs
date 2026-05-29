using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

// ============================================================================
// ASI-Evolve inspired: Parallel Experiment Runner
// Runs multiple evolution branches concurrently (2-4 workers).
// Each worker: select candidate → execute → analyze → update island.
// ============================================================================

public sealed record EvolutionBranchResult
{
    public string BranchId { get; init; } = "";
    public string CandidateId { get; init; } = "";
    public float Fitness { get; init; }
    public double LatencyMs { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
    public Dictionary<string, double> Metrics { get; init; } = new();
}

public sealed record EvolutionRound
{
    public int Round { get; init; }
    public List<EvolutionBranchResult> Results { get; init; } = new();
    public float BestFitness { get; init; }
    public float AvgFitness { get; init; }
    public double TotalLatencyMs { get; init; }
    public int SuccessCount { get; init; }
    public int FailureCount { get; init; }
}

public sealed class ParallelExperimentRunner
{
    private readonly IChatClient _llm;
    private readonly ExperimentAnalyzer _analyzer;
    private readonly IslandSampler _islandSampler;
    private readonly SynapticMemory _synapticMemory;
    private readonly ILogger<ParallelExperimentRunner> _logger;
    private readonly ConcurrentQueue<EvolutionRound> _roundHistory = new();
    private int _currentRound;
    private int _maxParallelWorkers;
    private const int MaxRoundHistory = 100;

    public ParallelExperimentRunner(
        IChatClient llm,
        ExperimentAnalyzer analyzer,
        IslandSampler islandSampler,
        SynapticMemory synapticMemory,
        int maxParallelWorkers = 4,
        ILogger<ParallelExperimentRunner>? logger = null)
    {
        _llm = llm;
        _analyzer = analyzer;
        _islandSampler = islandSampler;
        _synapticMemory = synapticMemory;
        _maxParallelWorkers = maxParallelWorkers;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ParallelExperimentRunner>.Instance;
    }

    // ========================================================================
    // 1. Run a full evolution round on multiple queries
    // ========================================================================

    public async Task<EvolutionRound> RunRoundAsync(
        List<string> queries,
        string domain = "general",
        int workers = 4,
        CancellationToken ct = default)
    {
        workers = Math.Min(workers, _maxParallelWorkers);
        var round = Interlocked.Increment(ref _currentRound);

        var islandSelection = _islandSampler.Select(domain, IslandSamplingAlgorithm.IslandElite);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var tasks = new List<Task<EvolutionBranchResult>>();
        var semaphore = new SemaphoreSlim(workers);

        foreach (var query in queries)
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    return await RunSingleBranchAsync(query, domain, islandSelection.CandidateId, ct);
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct));
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        sw.Stop();

        var bestFitness = results.MaxBy(r => r.Fitness)?.Fitness ?? 0;
        var avgFitness = results.Length > 0 ? results.Average(r => r.Fitness) : 0;

        var evolutionRound = new EvolutionRound
        {
            Round = round,
            Results = results.ToList(),
            BestFitness = bestFitness,
            AvgFitness = avgFitness,
            TotalLatencyMs = sw.ElapsedMilliseconds,
            SuccessCount = results.Count(r => r.Success),
            FailureCount = results.Count(r => !r.Success)
        };

        _roundHistory.Enqueue(evolutionRound);
        while (_roundHistory.Count > MaxRoundHistory)
            _roundHistory.TryDequeue(out _);

        // Record branch results to island sampler and analyzer
        foreach (var result in results)
        {
            _islandSampler.RegisterCandidate(domain, result.CandidateId, result.Fitness,
                diversity: Random.Shared.NextSingle());

            var trace = _analyzer.RecordTrace(
                query: $"evolve_{round}_{result.BranchId}",
                response: result.Fitness > 0.5f ? "success" : "failure",
                route: "evolution_round",
                complexity: 0.5f,
                confidence: result.Fitness,
                latencyMs: result.LatencyMs,
                toolCallCount: 1,
                toolSequence: new List<string> { "evolve" },
                errors: result.Error != null ? new List<string> { result.Error } : null,
                reward: result.Fitness,
                domain: domain,
                metrics: result.Metrics);

            if (result.Success)
                _analyzer.Analyze(trace);
        }

        _logger.LogInformation(
            "Evolution round {Round}: workers={Workers} queries={Queries} best={Best:F3} avg={Avg:F3} latency={Latency}ms",
            round, workers, queries.Count, bestFitness, avgFitness, sw.ElapsedMilliseconds);

        return evolutionRound;
    }

    // ========================================================================
    // 2. Run a single branch: Executes a query through the LLM and evaluates
    // ========================================================================

    private async Task<EvolutionBranchResult> RunSingleBranchAsync(
        string query, string domain, string parentCandidateId,
        CancellationToken ct)
    {
        var branchId = $"branch_{Guid.NewGuid():N}"[..10];
        var branchSw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var response = await _llm.GetResponseAsync(query,
                new ChatOptions { Temperature = 0.3f, MaxOutputTokens = 1024 }, ct)
                .ConfigureAwait(false);

            branchSw.Stop();
            var text = response.Text ?? "";

            var fitness = EvaluateFitness(text, domain);
            var metrics = new Dictionary<string, double>
            {
                ["response_length"] = text.Length,
                ["fitness"] = fitness,
                ["parent_candidate"] = parentCandidateId.GetHashCode()
            };

            _synapticMemory.Store(new SynapticExperience
            {
                Query = query,
                Response = text,
                Label = fitness > 0.5f ? "success" : "failure",
                Confidence = fitness,
                Reward = fitness,
                Metadata = $"evolve_branch={branchId},domain={domain},parent={parentCandidateId}",
                Type = SynapseType.Teaching
            });

            return new EvolutionBranchResult
            {
                BranchId = branchId,
                CandidateId = $"{domain}_{branchId}",
                Fitness = fitness,
                LatencyMs = branchSw.ElapsedMilliseconds,
                Success = fitness > 0.5f,
                Metrics = metrics
            };
        }
        catch (Exception ex)
        {
            branchSw.Stop();
            return new EvolutionBranchResult
            {
                BranchId = branchId,
                CandidateId = $"{domain}_{branchId}",
                Fitness = 0,
                LatencyMs = branchSw.ElapsedMilliseconds,
                Success = false,
                Error = ex.Message
            };
        }
    }

    // ========================================================================
    // 3. Fitness evaluation — domain-specific scoring
    // ========================================================================

    private static float EvaluateFitness(string response, string domain)
    {
        if (string.IsNullOrEmpty(response)) return 0;

        float score = 0.5f;

        score += Math.Min(0.3f, response.Length / 2000f * 0.2f);
        score += HasStructuredContent(response) ? 0.2f : 0;

        return domain switch
        {
            "code" => score + (response.Contains("class") || response.Contains("function") ? 0.1f : 0),
            "reasoning" => score + (response.Contains("因此") || response.Contains("because") ? 0.1f : 0),
            "math" => score + (response.Contains("=") ? 0.1f : 0),
            _ => score
        };
    }

    private static bool HasStructuredContent(string response)
    {
        return response.Contains("\n- ") || response.Contains("\n1. ") || response.Contains("\n#")
            || response.Contains("{") || response.Contains("```");
    }

    // ========================================================================
    // 4. Continued evolution: multi-round progressive optimization
    // ========================================================================

    public async Task<List<EvolutionRound>> EvolveAsync(
        string domain, int rounds, int queriesPerRound, int workers,
        CancellationToken ct = default)
    {
        var roundsList = new List<EvolutionRound>();

        for (int i = 0; i < rounds; i++)
        {
            if (ct.IsCancellationRequested) break;

            var previousFitness = _roundHistory.LastOrDefault()?.AvgFitness ?? 0;
            var queries = GenerateEvolveQueries(domain, queriesPerRound, previousFitness);

            var round = await RunRoundAsync(queries, domain, workers, ct).ConfigureAwait(false);
            roundsList.Add(round);

            if (round.BestFitness > 0.95f)
            {
                _logger.LogInformation("Evolution converged at round {Round} (fitness={Fitness:F3})",
                    round.Round, round.BestFitness);
                break;
            }
        }

        return roundsList;
    }

    private static List<string> GenerateEvolveQueries(string domain, int count, float previousFitness)
    {
        var queries = new List<string>();
        var baseQueries = domain switch
        {
            "code" => new[] { "Write a function to sort a list", "Implement a binary search", "Create a REST API endpoint" },
            "reasoning" => new[] { "Analyze the causes of climate change", "Compare two machine learning approaches", "Explain quantum computing" },
            "math" => new[] { "Solve a system of linear equations", "Calculate compound interest over 10 years", "Find the derivative of x^2" },
            _ => new[] { "Summarize recent advances in AI", "Explain how transformers work", "Describe blockchain technology" }
        };

        for (int i = 0; i < count; i++)
        {
            var baseQuery = baseQueries[i % baseQueries.Length];
            queries.Add(previousFitness > 0
                ? $"[Iteration {i}] Improve upon: {baseQuery}"
                : baseQuery);
        }

        return queries;
    }

    public Dictionary<string, object> GetStats() => new()
    {
        ["total_rounds"] = _currentRound,
        ["history_count"] = _roundHistory.Count,
        ["max_workers"] = _maxParallelWorkers,
        ["last_best_fitness"] = _roundHistory.LastOrDefault()?.BestFitness ?? 0,
        ["last_avg_fitness"] = _roundHistory.LastOrDefault()?.AvgFitness ?? 0
    };
}
