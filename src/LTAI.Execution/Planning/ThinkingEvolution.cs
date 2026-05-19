using System.Text.Json;
using LTAI.Execution.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Execution.Planning;

public sealed class ThinkingEvolution
{
    private const int DefaultPopulationSize = 32;
    private const int DefaultEliteSize = 2;
    private const int DefaultMaxGenerations = 24;
    private const double DefaultMutationRate = 0.3;
    private const double DefaultCrossoverRate = 0.5;
    private const int ConvergenceStallLimit = 5;

    private static ThinkingEvolution? _instance;
    private static readonly Lock InstanceLock = new();

    private readonly ILogger<ThinkingEvolution> _logger;
    private readonly Random _rng;
    private List<EvolutionCandidate> _previousPopulation;

    private int _populationSize;
    private int _eliteSize;
    private int _maxGenerations;
    private double _mutationRate;
    private double _crossoverRate;

    private int _generationsRun;
    private int _totalMutations;
    private int _totalCrossovers;
    private double _bestFitnessEver;
    private int _stallCount;

    public static readonly string[] KeywordPool =
    {
        "optimize", "refactor", "analyze", "verify", "decompose",
        "generalize", "simplify", "evaluate", "transform", "validate",
        "enhance", "reduce", "extract", "compose", "abstract"
    };

    private ThinkingEvolution(
        ILogger<ThinkingEvolution> logger,
        int populationSize = DefaultPopulationSize,
        int eliteSize = DefaultEliteSize,
        int maxGenerations = DefaultMaxGenerations,
        double mutationRate = DefaultMutationRate,
        double crossoverRate = DefaultCrossoverRate)
    {
        _logger = logger;
        _rng = new Random();
        _previousPopulation = new List<EvolutionCandidate>();

        _populationSize = populationSize;
        _eliteSize = eliteSize;
        _maxGenerations = maxGenerations;
        _mutationRate = mutationRate;
        _crossoverRate = crossoverRate;

        _generationsRun = 0;
        _totalMutations = 0;
        _totalCrossovers = 0;
        _bestFitnessEver = 0;
        _stallCount = 0;
    }

    public static ThinkingEvolution Instance => GetInstance(null);

    public static ThinkingEvolution GetInstance(
        ILogger<ThinkingEvolution>? logger = null,
        int populationSize = DefaultPopulationSize,
        int eliteSize = DefaultEliteSize,
        int maxGenerations = DefaultMaxGenerations,
        double mutationRate = DefaultMutationRate,
        double crossoverRate = DefaultCrossoverRate)
    {
        if (_instance is null)
        {
            lock (InstanceLock)
            {
                _instance ??= new ThinkingEvolution(
                    logger ?? new LoggerFactory().CreateLogger<ThinkingEvolution>(),
                    populationSize,
                    eliteSize,
                    maxGenerations,
                    mutationRate,
                    crossoverRate);
            }
        }
        return _instance;
    }

    public EvolutionResult EvolvePopulation(
        List<EvolutionCandidate> initialCandidates,
        Func<EvolutionCandidate, double> fitnessFn,
        Func<EvolutionCandidate, bool>? qualityCheckFn = null)
    {
        _generationsRun = 0;
        _totalMutations = 0;
        _totalCrossovers = 0;
        _bestFitnessEver = 0;
        _stallCount = 0;

        var population = initialCandidates
            .Select(c =>
            {
                c.Generation = 0;
                return c;
            })
            .ToList();

        if (qualityCheckFn is not null)
        {
            population = population.Where(c => qualityCheckFn(c)).ToList();
        }

        while (population.Count < _populationSize)
        {
            population.Add(CreateCandidateFrom(population));
        }

        var elitePool = new List<EvolutionCandidate>();
        var initialBest = CalculateFitness(population, fitnessFn);
        _bestFitnessEver = initialBest;

        for (var gen = 0; gen < _maxGenerations; gen++)
        {
            _generationsRun = gen + 1;

            CalculateFitness(population, fitnessFn);

            population = population
                .OrderByDescending(c => c.Fitness)
                .ToList();

            if (qualityCheckFn is not null)
            {
                foreach (var c in population)
                {
                    if (!qualityCheckFn(c))
                    {
                        c.Fitness = 0;
                    }
                }
                population = population
                    .OrderByDescending(c => c.Fitness)
                    .ToList();
            }

            var elites = population
                .Take(_eliteSize)
                .Select(c => CloneCandidate(c, gen))
                .ToList();

            foreach (var e in elites)
            {
                if (!elitePool.Any(x => x.Id == e.Id))
                {
                    elitePool.Add(e);
                }
            }

            var currentBest = population[0].Fitness;
            if (currentBest > _bestFitnessEver)
            {
                _bestFitnessEver = currentBest;
                _stallCount = 0;
            }
            else
            {
                _stallCount++;
            }

            _logger.LogDebug(
                "Generation {Gen}/{MaxGen} best_fitness={Fit:F4} avg_fitness={Avg:F4} diversity={Div:F4}",
                gen + 1, _maxGenerations, currentBest,
                population.Average(c => c.Fitness),
                ComputeDiversityScore(population));

            if (_stallCount >= ConvergenceStallLimit)
            {
                _logger.LogInformation(
                    "Convergence reached after {Gen} generations (stalled for {Stall})",
                    gen + 1, _stallCount);
                break;
            }

            var nextGen = new List<EvolutionCandidate>(elites);

            while (nextGen.Count < _populationSize)
            {
                if (_rng.NextDouble() < _crossoverRate && population.Count >= 2)
                {
                    var parents = SelectPair(population);
                    var child = Crossover(parents.a, parents.b);
                    child.Generation = gen + 1;
                    _totalCrossovers++;

                    if (_rng.NextDouble() < _mutationRate)
                    {
                        child = Mutate(child, PickMutationDirection());
                        _totalMutations++;
                    }

                    nextGen.Add(child);
                }
                else if (_rng.NextDouble() < _mutationRate && population.Count > 0)
                {
                    var parent = SelectOne(population);
                    var mutant = Mutate(parent, PickMutationDirection());
                    mutant.Generation = gen + 1;
                    _totalMutations++;
                    nextGen.Add(mutant);
                }
                else
                {
                    nextGen.Add(CloneCandidate(population[_rng.Next(population.Count)], gen + 1));
                }
            }

            if (nextGen.Count < _populationSize)
            {
                while (nextGen.Count < _populationSize)
                {
                    nextGen.Add(CreateCandidateFrom(population));
                }
            }

            population = nextGen;
        }

        _previousPopulation = population.ToList();

        var finalCandidates = population
            .OrderByDescending(c => c.Fitness)
            .ToList();

        return new EvolutionResult
        {
            Candidates = finalCandidates,
            ElitePool = elitePool,
            DiversityScore = ComputeDiversityScore(finalCandidates)
        };
    }

    public EvolutionCandidate Mutate(EvolutionCandidate candidate, string direction = "explore")
    {
        var content = candidate.Content;
        var result = direction switch
        {
            "explore" => MutateExplore(content),
            "refine" => MutateRefine(content),
            "diversify" => MutateDiversify(content),
            _ => MutateExplore(content)
        };

        return new EvolutionCandidate
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Content = result,
            Generation = candidate.Generation,
            Fitness = 0,
            Annotations = candidate.Annotations.ToList(),
            ParentIds = new List<string> { candidate.Id },
            MutationCount = candidate.MutationCount + 1
        };
    }

    public EvolutionCandidate Crossover(EvolutionCandidate parentA, EvolutionCandidate parentB)
    {
        var linesA = SplitLines(parentA.Content);
        var linesB = SplitLines(parentB.Content);
        var fused = new List<string>();
        var maxLines = Math.Max(linesA.Count, linesB.Count);

        for (var i = 0; i < maxLines; i++)
        {
            if (i % 2 == 0 && i < linesA.Count)
                fused.Add(linesA[i]);
            else if (i < linesB.Count)
                fused.Add(linesB[i]);
            else if (i < linesA.Count)
                fused.Add(linesA[i]);
        }

        return new EvolutionCandidate
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Content = string.Join("\n", fused),
            Generation = 0,
            Fitness = 0,
            Annotations = new List<string> { "crossover" },
            ParentIds = new List<string> { parentA.Id, parentB.Id },
            MutationCount = Math.Max(parentA.MutationCount, parentB.MutationCount)
        };
    }

    public EvolutionCandidate Recombine(List<EvolutionCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return new EvolutionCandidate
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Content = "",
                Generation = 0,
                Fitness = 0
            };
        }

        var merged = new List<string>();
        var maxLines = candidates.Max(c => SplitLines(c.Content).Count);

        for (var i = 0; i < candidates.Count && i < maxLines; i++)
        {
            var lines = SplitLines(candidates[i].Content);
            if (i < lines.Count)
            {
                merged.Add(lines[i]);
            }
        }

        return new EvolutionCandidate
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Content = string.Join("\n", merged),
            Generation = 0,
            Fitness = 0,
            Annotations = new List<string> { "recombined" },
            ParentIds = candidates.Select(c => c.Id).ToList(),
            MutationCount = candidates.Max(c => c.MutationCount)
        };
    }

    public List<EvolutionCandidate> RankByPromptEcho(
        List<EvolutionCandidate> candidates,
        EvolutionCandidate original)
    {
        if (candidates.Count == 0)
            return candidates;

        var scored = candidates
            .Select(c => (candidate: c, score: NGramOverlapScore(c.Content, original.Content)))
            .OrderByDescending(x => x.score)
            .Select(x =>
            {
                x.candidate.Fitness = x.score;
                return x.candidate;
            })
            .ToList();

        return scored;
    }

    public double ComputeDiversityScore(List<EvolutionCandidate> population)
    {
        if (population.Count < 2)
            return 0;

        double totalSimilarity = 0;
        var pairs = 0;

        for (var i = 0; i < population.Count; i++)
        {
            for (var j = i + 1; j < population.Count; j++)
            {
                totalSimilarity += JaccardSimilarity(population[i].Content, population[j].Content);
                pairs++;
            }
        }

        return pairs > 0 ? 1.0 - (totalSimilarity / pairs) : 0;
    }

    public Dictionary<string, object?> GetStats()
    {
        var avgFitness = _previousPopulation.Count > 0
            ? _previousPopulation.Average(c => c.Fitness)
            : 0.0;

        var avgMutationCount = _previousPopulation.Count > 0
            ? _previousPopulation.Average(c => c.MutationCount)
            : 0.0;

        return new Dictionary<string, object?>
        {
            ["generations_run"] = _generationsRun,
            ["total_mutations"] = _totalMutations,
            ["total_crossovers"] = _totalCrossovers,
            ["population_size"] = _previousPopulation.Count,
            ["best_fitness"] = _bestFitnessEver,
            ["avg_fitness"] = avgFitness,
            ["avg_mutation_count"] = avgMutationCount,
            ["diversity_score"] = ComputeDiversityScore(_previousPopulation),
            ["stall_count"] = _stallCount,
            ["converged"] = _stallCount >= ConvergenceStallLimit
        };
    }

    private string PickMutationDirection()
    {
        var roll = _rng.NextDouble();
        if (roll < 0.5) return "explore";
        if (roll < 0.8) return "refine";
        return "diversify";
    }

    private string MutateExplore(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return KeywordPool[_rng.Next(KeywordPool.Length)];

        var chars = content.ToCharArray();
        if (chars.Length < 2)
            return content + " " + KeywordPool[_rng.Next(KeywordPool.Length)];

        var pos = _rng.Next(chars.Length - 1);
        var op = _rng.Next(3);

        switch (op)
        {
            case 0: // swap
                (chars[pos], chars[pos + 1]) = (chars[pos + 1], chars[pos]);
                return new string(chars);
            case 1: // insert
                var insertChar = (char)('a' + _rng.Next(26));
                var result = content[..pos] + insertChar + content[pos..];
                return result;
            case 2: // delete
                if (content.Length <= 1)
                    return content + " " + KeywordPool[_rng.Next(KeywordPool.Length)];
                var deletePos = _rng.Next(content.Length);
                return content[..deletePos] + content[(deletePos + 1)..];
            default:
                return content;
        }
    }

    private string MutateRefine(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return KeywordPool[_rng.Next(KeywordPool.Length)];

        var sentences = SplitSentences(content);
        if (sentences.Count <= 1)
            return content;

        var scored = sentences
            .Select(s => (sentence: s, score: s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length))
            .OrderBy(x => x.score)
            .ToList();

        var removeCount = Math.Max(1, sentences.Count / 4);
        for (var i = 0; i < removeCount && scored.Count > 0; i++)
        {
            scored.RemoveAt(0);
        }

        return string.Join(" ", scored.Select(x => x.sentence));
    }

    private string MutateDiversify(string content)
    {
        var keyword = KeywordPool[_rng.Next(KeywordPool.Length)];
        if (string.IsNullOrWhiteSpace(content))
            return keyword;

        var insertionPoint = _rng.Next(2);
        return insertionPoint switch
        {
            0 => keyword + " " + content,
            1 => content + " " + keyword,
            _ => keyword + " " + content
        };
    }

    private static List<string> SplitLines(string content)
    {
        if (string.IsNullOrEmpty(content))
            return new List<string>();

        return content.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
    }

    private static List<string> SplitSentences(string content)
    {
        if (string.IsNullOrEmpty(content))
            return new List<string>();

        return content.Split(new[] { '.', '!', '?', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    private static double CalculateFitness(List<EvolutionCandidate> population, Func<EvolutionCandidate, double> fitnessFn)
    {
        double max = double.MinValue;
        foreach (var c in population)
        {
            c.Fitness = fitnessFn(c);
            if (c.Fitness > max)
                max = c.Fitness;
        }
        return max;
    }

    private (EvolutionCandidate a, EvolutionCandidate b) SelectPair(List<EvolutionCandidate> population)
    {
        var a = SelectOne(population);
        EvolutionCandidate b;
        var attempts = 0;
        do
        {
            b = SelectOne(population);
            attempts++;
        } while (b.Id == a.Id && attempts < 10);

        return (a, b);
    }

    private EvolutionCandidate SelectOne(List<EvolutionCandidate> population)
    {
        var idx = _rng.Next(population.Count);
        return population[idx];
    }

    private EvolutionCandidate CloneCandidate(EvolutionCandidate source, int generation)
    {
        return new EvolutionCandidate
        {
            Id = source.Id,
            Content = source.Content,
            Generation = generation,
            Fitness = source.Fitness,
            Annotations = source.Annotations.ToList(),
            ParentIds = source.ParentIds.ToList(),
            MutationCount = source.MutationCount
        };
    }

    private EvolutionCandidate CreateCandidateFrom(List<EvolutionCandidate> population)
    {
        if (population.Count == 0)
        {
            return new EvolutionCandidate
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Content = KeywordPool[_rng.Next(KeywordPool.Length)],
                Generation = 0,
                Fitness = 0
            };
        }

        var source = population[_rng.Next(population.Count)];
        return Mutate(source, "explore");
    }

    private static double NGramOverlapScore(string candidate, string original, int n = 3)
    {
        if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(original))
            return 0;

        var candGrams = GetNGrams(candidate.ToLowerInvariant(), n);
        var origGrams = GetNGrams(original.ToLowerInvariant(), n);

        if (origGrams.Count == 0)
            return 0;

        var intersection = candGrams.Intersect(origGrams).Count();
        return (double)intersection / origGrams.Count;
    }

    private static HashSet<string> GetNGrams(string text, int n)
    {
        var grams = new HashSet<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i <= words.Length - n; i++)
        {
            grams.Add(string.Join(" ", words[i..(i + n)]));
        }
        return grams;
    }

    private static double JaccardSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b))
            return 1.0;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return 0;

        var setA = new HashSet<string>(a.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var setB = new HashSet<string>(b.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        var intersection = setA.Intersect(setB).Count();
        var union = setA.Union(setB).Count();

        return union > 0 ? (double)intersection / union : 0;
    }
}
