using LTAI.DNA.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.DNA.Evolution;

public sealed class EvolutionDriver
{
    private readonly ILogger<EvolutionDriver> _logger;
    private readonly List<Genome> _population = new();
    private readonly Random _rng = new();
    private EvolutionPhase _phase = EvolutionPhase.Embryonic;
    private int _generation;

    public Genome CurrentGenome { get; private set; }
    public EvolutionPhase Phase => _phase;
    public IReadOnlyList<Genome> Population => _population.AsReadOnly();

    public EvolutionDriver(ILogger<EvolutionDriver> logger)
    {
        _logger = logger;
        CurrentGenome = CreateSeedGenome();
    }

    public async Task EvolveAsync(
        Dictionary<string, double> fitnessSignals,
        CancellationToken cancellationToken = default)
    {
        _generation++;

        ApplyFitnessSignals(CurrentGenome, fitnessSignals);

        if (_generation % 10 == 0)
            Mutate(CurrentGenome);

        if (_generation % 50 == 0)
            Crossover();

        if (_generation >= 500 && _phase < EvolutionPhase.Integration)
            AdvancePhase();

        PrunePopulation();

        _logger.LogInformation("Evolution: gen={Gen}, phase={Phase}, fitness={Fitness:F3}, pop={Pop}",
            _generation, _phase, CurrentGenome.FitnessScore, _population.Count);

        await Task.CompletedTask;
    }

    public async Task<Genome> ForkGenomeAsync(string reason, CancellationToken cancellationToken = default)
    {
        var child = CloneGenome(CurrentGenome);
        child.Id = Guid.NewGuid().ToString("N");
        child.CreatedAt = DateTime.UtcNow;
        child.Generation = _generation;
        _population.Add(child);
        _logger.LogInformation("Genome forked: {Id}, reason: {Reason}", child.Id, reason);
        return await Task.FromResult(child);
    }

    public Dictionary<string, double> GetFitnessComponents()
    {
        return CurrentGenome.Genes.ToDictionary(g => g.Key, g => g.Value.Expression * g.Value.FitnessScore);
    }

    private Genome CreateSeedGenome()
    {
        var genome = new Genome { Generation = 0 };
        var seedGenes = new Dictionary<string, Gene>
        {
            ["curiosity"] = new() { Name = "curiosity", Expression = 0.7, MutationRate = 0.02 },
            ["creativity"] = new() { Name = "creativity", Expression = 0.6, MutationRate = 0.03 },
            ["precision"] = new() { Name = "precision", Expression = 0.8, MutationRate = 0.01 },
            ["adaptability"] = new() { Name = "adaptability", Expression = 0.7, MutationRate = 0.03 },
            ["cooperation"] = new() { Name = "cooperation", Expression = 0.6, MutationRate = 0.02 },
            ["efficiency"] = new() { Name = "efficiency", Expression = 0.75, MutationRate = 0.02 },
            ["exploration"] = new() { Name = "exploration", Expression = 0.65, MutationRate = 0.04 },
            ["stability"] = new() { Name = "stability", Expression = 0.7, MutationRate = 0.01 }
        };
        genome.Genes = seedGenes;
        genome.FitnessScore = 0.5;
        return genome;
    }

    private void ApplyFitnessSignals(Genome genome, Dictionary<string, double> signals)
    {
        foreach (var (geneName, signal) in signals)
        {
            if (genome.Genes.TryGetValue(geneName, out var gene))
            {
                gene.Expression = Math.Clamp(
                    gene.Expression * 0.9 + signal * 0.1,
                    0.01, 1.0);
            }
        }

        genome.FitnessScore = genome.Genes.Values.Average(g => g.Expression);
    }

    private void Mutate(Genome genome)
    {
        foreach (var (name, gene) in genome.Genes)
        {
            if (_rng.NextDouble() < gene.MutationRate)
            {
                var delta = (_rng.NextDouble() - 0.5) * 0.2;
                var oldExpr = gene.Expression;
                gene.Expression = Math.Clamp(gene.Expression + delta, 0.01, 1.0);

                genome.MutationHistory.Add(new MutationRecord
                {
                    Gene = name,
                    OldExpression = oldExpr,
                    NewExpression = gene.Expression,
                    Trigger = "random_drift",
                    FitnessDelta = gene.Expression - oldExpr
                });
            }
        }

        _logger.LogDebug("Mutation: {Count} genes affected", genome.MutationHistory
            .Count(m => m.Timestamp > DateTime.UtcNow.AddSeconds(-1)));
    }

    private void Crossover()
    {
        if (_population.Count < 2) return;

        var top = _population.OrderByDescending(g => g.FitnessScore).Take(2).ToList();
        var parent1 = top[0];
        var parent2 = top[1];

        var child = CloneGenome(parent1);
        child.Id = Guid.NewGuid().ToString("N");
        child.Generation = _generation;

        foreach (var (name, gene) in child.Genes)
        {
            if (_rng.NextDouble() < 0.5 && parent2.Genes.TryGetValue(name, out var other))
                gene.Expression = (gene.Expression + other.Expression) / 2.0;
        }

        _population.Add(child);
        _logger.LogInformation("Crossover: child {Id} from {P1} x {P2}",
            child.Id, parent1.Id[..8], parent2.Id[..8]);
    }

    private void AdvancePhase()
    {
        if ((int)_phase < Enum.GetValues<EvolutionPhase>().Length - 1)
        {
            _phase = (EvolutionPhase)((int)_phase + 1);
            _logger.LogInformation("Evolution phase advanced: {Phase}", _phase);
        }
    }

    private void PrunePopulation()
    {
        if (_population.Count <= 10) return;

        var keep = _population
            .OrderByDescending(g => g.FitnessScore)
            .Take(10)
            .ToList();

        _population.Clear();
        _population.AddRange(keep);
    }

    private static Genome CloneGenome(Genome source)
    {
        return new Genome
        {
            Id = source.Id,
            Version = source.Version,
            Generation = source.Generation,
            FitnessScore = source.FitnessScore,
            Genes = source.Genes.ToDictionary(
                g => g.Key,
                g => new Gene
                {
                    Name = g.Value.Name,
                    Expression = g.Value.Expression,
                    Stability = g.Value.Stability,
                    MutationRate = g.Value.MutationRate,
                    Interactions = new Dictionary<string, double>(g.Value.Interactions)
                }),
            ActivatedPathways = new List<string>(source.ActivatedPathways)
        };
    }
}

public sealed class SwarmEvolution
{
    private readonly ILogger<SwarmEvolution> _logger;
    private readonly List<SwarmAgent> _swarm = new();
    private readonly Random _rng = new();

    public SwarmEvolution(ILogger<SwarmEvolution> logger)
    {
        _logger = logger;
    }

    public async Task<List<string>> OptimizeAsync(
        string problem,
        List<string> candidateSolutions,
        int generations = 5,
        CancellationToken cancellationToken = default)
    {
        _swarm.Clear();
        foreach (var sol in candidateSolutions)
        {
            _swarm.Add(new SwarmAgent
            {
                Solution = sol,
                Fitness = EvaluateSolution(sol, problem),
                Velocity = _rng.NextDouble() * 0.2
            });
        }

        for (var gen = 0; gen < generations; gen++)
        {
            var globalBest = _swarm.MaxBy(a => a.Fitness);

            foreach (var agent in _swarm)
            {
                var cognitive = _rng.NextDouble() * 0.5;
                var social = _rng.NextDouble() * 0.3;

                agent.Velocity = agent.Velocity * 0.7 + cognitive + social;
                agent.Fitness = EvaluateSolution(agent.Solution, problem);

                if (_rng.NextDouble() < 0.05)
                {
                    agent.Solution = MutateSolution(agent.Solution);
                    agent.Fitness = EvaluateSolution(agent.Solution, problem);
                }
            }

            _logger.LogDebug("Swarm gen {Gen}: best fitness={Fitness:F3}", gen,
                _swarm.Max(a => a.Fitness));
        }

        return await Task.FromResult(_swarm
            .OrderByDescending(a => a.Fitness)
            .Select(a => a.Solution)
            .Take(3)
            .ToList());
    }

    private static double EvaluateSolution(string solution, string problem)
    {
        var sharedWords = solution.Split(' ')
            .Count(w => problem.Contains(w, StringComparison.OrdinalIgnoreCase));
        return Math.Min(1.0, sharedWords * 0.1 + solution.Length * 0.001);
    }

    private string MutateSolution(string solution)
    {
        var words = solution.Split(' ').ToList();
        if (words.Count == 0) return solution;
        var idx = _rng.Next(words.Count);
        words[idx] = words[idx].Length > 1
            ? words[idx][..^1] + (char)('a' + _rng.Next(26))
            : words[idx];
        return string.Join(" ", words);
    }

    private sealed class SwarmAgent
    {
        public string Solution { get; set; } = "";
        public double Fitness { get; set; }
        public double Velocity { get; set; }
    }
}
