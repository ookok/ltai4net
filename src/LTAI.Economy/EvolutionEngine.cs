using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace LTAI.Economy;

public sealed record EvolutionCandidate(
    string Id,
    string Code,
    double FitnessScore,
    double CorrectnessScore,
    double SecurityScore,
    double LatencyMs,
    int Generation,
    DateTime CreatedAt,
    string MutationType,
    string ParentId,
    Dictionary<string, double> ProfilingMetrics)
{
    public bool IsValid => CorrectnessScore >= 1.0 && SecurityScore >= 1.0;
    public string MutationChain => $"{ParentId}->{Id}({MutationType})";
}

public sealed record EvolutionConfig(
    int PopulationSize = 50,
    int MaxGenerations = 100,
    int EliteCount = 5,
    int TournamentSize = 10,
    double MutationRate = 0.3,
    double CrossoverRate = 0.2,
    double ConvergenceThreshold = 0.001,
    int ConvergenceWindow = 10,
    bool UseCoEvolution = false,
    int Controllers = 10,
    int EvaluatorsPerController = 10)
{
    public static EvolutionConfig Default => new();
}

public enum EvolutionPhase
{
    Initializing,
    Generating,
    Evaluating,
    Ranking,
    Selecting,
    Mutating,
    Converged,
    Stopped
}

public sealed class EvolutionEngine
{
    private readonly EvolutionConfig _config;
    private readonly PromptPool _promptPool;
    private readonly TieredEvaluator _evaluator;
    private readonly IChatClient _chatClient;
    private readonly ConcurrentDictionary<string, EvolutionCandidate> _population = new();
    private readonly ConcurrentQueue<EvolutionCandidate> _archive = new();
    private readonly ConcurrentDictionary<int, List<double>> _fitnessHistory = new();
    private readonly ConcurrentDictionary<string, int> _diversityIslands = new();
    private int _generation;
    private EvolutionPhase _phase = EvolutionPhase.Initializing;

    public event Action<string, EvolutionPhase, double>? OnPhaseChange;
    public event Action<EvolutionCandidate, double>? OnBestCandidate;

    public EvolutionEngine(
        EvolutionConfig config,
        PromptPool promptPool,
        TieredEvaluator evaluator,
        IChatClient chatClient)
    {
        _config = config;
        _promptPool = promptPool;
        _evaluator = evaluator;
        _chatClient = chatClient;
    }

    public async Task<EvolutionCandidate?> RunAsync(
        string initialCode,
        CancellationToken ct = default)
    {
        SeedPopulation(initialCode);

        double bestScore = 0;
        var noImprovement = 0;

        for (_generation = 0; _generation < _config.MaxGenerations; _generation++)
        {
            if (ct.IsCancellationRequested) break;

            _phase = EvolutionPhase.Generating;
            var newCandidates = await GenerateCandidatesAsync(ct).ConfigureAwait(false);

            _phase = EvolutionPhase.Evaluating;
            var evaluated = await EvaluateBatchAsync(newCandidates, ct).ConfigureAwait(false);

            foreach (var candidate in evaluated)
            {
                _population[candidate.Id] = candidate;
                _archive.Enqueue(candidate);
            }

            _phase = EvolutionPhase.Ranking;
            var ranked = RankPopulation();

            TrackFitness(ranked);

            _phase = EvolutionPhase.Selecting;
            var survivors = SelectSurvivors(ranked);

            if (survivors.Count > 0)
            {
                var currentBest = survivors[0];
                if (currentBest.FitnessScore > bestScore + _config.ConvergenceThreshold)
                {
                    bestScore = currentBest.FitnessScore;
                    noImprovement = 0;
                    OnBestCandidate?.Invoke(currentBest, bestScore);
                }
                else
                {
                    noImprovement++;
                }
            }

            if (noImprovement >= _config.ConvergenceWindow)
            {
                _phase = EvolutionPhase.Converged;
                OnPhaseChange?.Invoke("converged", _phase, bestScore);
                break;
            }

            _phase = EvolutionPhase.Mutating;
            PrunePopulation(survivors);

            OnPhaseChange?.Invoke($"gen_{_generation}", _phase, bestScore);
        }

        _phase = EvolutionPhase.Stopped;
        return GetBestCandidate();
    }

    public EvolutionCandidate? GetBestCandidate()
    {
        return _population.Values
            .Where(c => c.IsValid)
            .MaxBy(c => c.FitnessScore);
    }

    public List<EvolutionCandidate> GetPopulation()
    {
        return _population.Values.ToList();
    }

    public Dictionary<string, double> GetDiversityMetrics()
    {
        var result = new Dictionary<string, double>();
        foreach (var (island, count) in _diversityIslands)
        {
            result[island] = (double)count / Math.Max(1, _population.Count);
        }
        result["total_population"] = _population.Count;
        result["total_archive"] = _archive.Count;
        result["generation"] = _generation;
        return result;
    }

    public List<EvolutionCandidate> GetArchive()
    {
        return _archive.ToList();
    }

    private void SeedPopulation(string initialCode)
    {
        var seed = new EvolutionCandidate(
            Id: $"seed_{Guid.NewGuid():N}",
            Code: initialCode,
            FitnessScore: 0,
            CorrectnessScore: 0,
            SecurityScore: 0,
            LatencyMs: double.MaxValue,
            Generation: 0,
            CreatedAt: DateTime.UtcNow,
            MutationType: "seed",
            ParentId: "root",
            ProfilingMetrics: new());

        _population[seed.Id] = seed;

        for (int i = 1; i < _config.PopulationSize; i++)
        {
            var variant = new EvolutionCandidate(
                Id: $"seed_{Guid.NewGuid():N}",
                Code: initialCode,
                FitnessScore: 0,
                CorrectnessScore: 0,
                SecurityScore: 0,
                LatencyMs: double.MaxValue,
                Generation: 0,
                CreatedAt: DateTime.UtcNow,
                MutationType: "seed_variant",
                ParentId: "root",
                ProfilingMetrics: new());

            _population[variant.Id] = variant;
        }
    }

    private async Task<List<EvolutionCandidate>> GenerateCandidatesAsync(CancellationToken ct)
    {
        var candidates = new List<EvolutionCandidate>();
        var tournament = TournamentSelect(_config.TournamentSize);

        foreach (var parent in tournament)
        {
            if (ct.IsCancellationRequested) break;

            var prompt = _promptPool.Sample();
            var mutationType = prompt.Contains("unroll") ? "unroll" :
                               prompt.Contains("schedule") ? "schedule" :
                               prompt.Contains("tiling") ? "tiling" :
                               prompt.Contains("cast") ? "type_opt" :
                               prompt.Contains("layout") ? "layout" :
                               "general";

            var systemPrompt = BuildSystemPrompt(parent, mutationType);

            try
            {
                var response = await _chatClient.GetResponseAsync(
                    new List<ChatMessage>
                    {
                        new(ChatRole.System, systemPrompt),
                        new(ChatRole.User, prompt)
                    },
                    cancellationToken: ct).ConfigureAwait(false);

                var generatedCode = ExtractCodeBlock(response.Text ?? "");

                if (!string.IsNullOrWhiteSpace(generatedCode) && generatedCode != parent.Code)
                {
                    candidates.Add(new EvolutionCandidate(
                        Id: $"gen{_generation}_{Guid.NewGuid():N}",
                        Code: generatedCode,
                        FitnessScore: 0,
                        CorrectnessScore: 0,
                        SecurityScore: 0,
                        LatencyMs: double.MaxValue,
                        Generation: _generation + 1,
                        CreatedAt: DateTime.UtcNow,
                        MutationType: mutationType,
                        ParentId: parent.Id,
                        ProfilingMetrics: new()));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch { /* non-fatal */ }
        }

        ClassifyDiversityIslands(candidates);
        return candidates;
    }

    private async Task<List<EvolutionCandidate>> EvaluateBatchAsync(
        List<EvolutionCandidate> candidates,
        CancellationToken ct)
    {
        var evaluated = new List<EvolutionCandidate>();

        var batches = candidates.Chunk(Math.Max(1, _config.EvaluatorsPerController));
        var tasks = new List<Task<EvolutionCandidate>>();

        foreach (var batch in batches)
        {
            if (ct.IsCancellationRequested) break;

            foreach (var candidate in batch)
            {
                tasks.Add(Task.Run(async () =>
                {
                    var result = await _evaluator.EvaluateAsync(candidate, ct).ConfigureAwait(false);
                    return candidate with
                    {
                        CorrectnessScore = result.CorrectnessScore,
                        SecurityScore = result.SecurityScore,
                        LatencyMs = result.LatencyMs,
                        FitnessScore = result.FitnessScore,
                        ProfilingMetrics = result.ProfilingMetrics
                    };
                }, ct));
            }

            var batchResults = await Task.WhenAll(tasks).ConfigureAwait(false);
            evaluated.AddRange(batchResults);
            tasks.Clear();
        }

        return evaluated;
    }

    private List<EvolutionCandidate> RankPopulation()
    {
        return _population.Values
            .OrderByDescending(c => c.FitnessScore)
            .ToList();
    }

    private List<EvolutionCandidate> SelectSurvivors(List<EvolutionCandidate> ranked)
    {
        var survivors = new List<EvolutionCandidate>();

        var elites = ranked
            .Where(c => c.IsValid)
            .Take(_config.EliteCount)
            .ToList();

        survivors.AddRange(elites);

        var validPool = ranked.Where(c => c.IsValid).ToList();
        while (survivors.Count < _config.PopulationSize && validPool.Count > 0)
        {
            var selected = TournamentSelectFromPool(validPool, _config.TournamentSize);
            foreach (var s in selected)
            {
                if (survivors.Count >= _config.PopulationSize) break;
                if (!survivors.Any(existing => existing.Id == s.Id))
                    survivors.Add(s);
            }
        }

        return survivors;
    }

    private void PrunePopulation(List<EvolutionCandidate> survivors)
    {
        _population.Clear();
        foreach (var survivor in survivors)
        {
            _population[survivor.Id] = survivor;
        }
    }

    private void TrackFitness(List<EvolutionCandidate> ranked)
    {
        var fitnesses = ranked.Select(r => r.FitnessScore).ToList();
        _fitnessHistory[_generation] = fitnesses;

        while (_fitnessHistory.Count > _config.ConvergenceWindow * 2)
        {
            var minGen = _fitnessHistory.Keys.Min();
            _fitnessHistory.TryRemove(minGen, out _);
        }
    }

    private List<EvolutionCandidate> TournamentSelect(int tournamentSize)
    {
        var pool = _population.Values.ToList();
        return TournamentSelectFromPool(pool, tournamentSize);
    }

    private static List<EvolutionCandidate> TournamentSelectFromPool(
        List<EvolutionCandidate> pool, int tournamentSize)
    {
        var selected = new List<EvolutionCandidate>();
        var rng = Random.Shared;

        for (int i = 0; i < tournamentSize && pool.Count > 0; i++)
        {
            var idx = rng.Next(pool.Count);
            selected.Add(pool[idx]);
        }

        return selected.OrderByDescending(c => c.FitnessScore).ToList();
    }

    private void ClassifyDiversityIslands(List<EvolutionCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            var island = DetermineIsland(candidate);
            _diversityIslands.AddOrUpdate(island, 1, (_, v) => v + 1);
        }
    }

    private static string DetermineIsland(EvolutionCandidate candidate)
    {
        if (candidate.Code.Length < 500) return "compact";
        if (candidate.Code.Length > 2000) return "complex";
        if (candidate.Code.Contains("unroll") || candidate.Code.Contains("for ")) return "loops";
        if (candidate.Code.Contains("reshape") || candidate.Code.Contains("layout")) return "layout";
        if (candidate.Code.Contains("cast") || candidate.Code.Contains("dtype")) return "type_ops";
        return "general";
    }

    private string BuildSystemPrompt(EvolutionCandidate parent, string mutationType)
    {
        var profInfo = parent.ProfilingMetrics.Count > 0
            ? $"\nCurrent profiling: {string.Join(", ", parent.ProfilingMetrics.Select(kv => $"{kv.Key}={kv.Value:F2}"))}"
            : "";

        return $"""
            You are an expert in hardware-aware kernel optimization and evolutionary code improvement.
            Your role is to mutate and improve the code while preserving functional correctness.
            
            Current latency: {parent.LatencyMs:F2} ms
            Target mutation type: {mutationType}{profInfo}
            
            Rules:
            1. Preserve the functional correctness of the algorithm
            2. Do not change cryptographic security parameters
            3. Focus on the specified mutation type: {mutationType}
            4. Optimize for lower latency on target hardware
            5. Keep changes focused and minimal - one optimization at a time
            6. Return ONLY the optimized code block, no explanations
            
            Current code for reference:
            ```python
            {parent.Code[..Math.Min(parent.Code.Length, 2000)]}
            ```
            
            Generate an improved version focusing on {mutationType} optimization:
            """;
    }

    private static string ExtractCodeBlock(string llmResponse)
    {
        var codeStart = llmResponse.IndexOf("```");
        if (codeStart < 0) return llmResponse.Trim();

        var headerEnd = llmResponse.IndexOf('\n', codeStart);
        if (headerEnd < 0) return llmResponse[(codeStart + 3)..].Trim();

        var codeEnd = llmResponse.IndexOf("```", headerEnd);
        if (codeEnd < 0) return llmResponse[(headerEnd + 1)..].Trim();

        return llmResponse[(headerEnd + 1)..codeEnd].Trim();
    }
}
