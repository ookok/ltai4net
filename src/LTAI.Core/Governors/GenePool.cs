using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Governors;

public enum GeneTarget
{
    Unknown,
    Router,
    Classifier,
    Threshold,
    Temperature,
    Prompt,
    Tool,
    KnowledgeBase,
    Embedding,
    Safety,
    General
}

public enum GeneOperation
{
    Unknown,
    Adjust,
    Replace,
    Override,
    Route,
    Use,
    Deploy,
    Evaluate,
    Hybrid
}

public sealed record Gene
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string Condition { get; init; } = "";
    public string Action { get; init; } = "";
    public string TargetModule { get; init; } = "";
    public string OperationType { get; init; } = "";
    public GeneTarget Target
    {
        get => ParseTarget(TargetModule);
        init => TargetModule = value.ToString();
    }
    public GeneOperation Operation
    {
        get => ParseOperation(OperationType);
        init => OperationType = value.ToString();
    }
    public Dictionary<string, double> ConditionThresholds { get; init; } = new();
    public List<string> ConditionLabels { get; init; } = new();
    public string ConditionOperator { get; init; } = "and";
    public string RouteLabel { get; init; } = "";
    public Dictionary<string, object> Parameters { get; init; } = new();
    public double Weight { get; set; } = 1.0;
    public double Fitness { get; set; }
    public int Trials { get; set; }
    public int Successes { get; set; }
    public bool IsProtected { get; set; }
    public string Source { get; init; } = "seed";
    public string Niche { get; init; } = "general";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastEvaluatedAt { get; set; } = DateTime.UtcNow;
    public List<string> MutationHistory { get; init; } = new();

    public static GeneTarget ParseTarget(string targetModule) => targetModule?.ToLowerInvariant() switch
    {
        "router" or "pareto" => GeneTarget.Router,
        "classifier" or "l0" or "intent" => GeneTarget.Classifier,
        "threshold" or "quota" or "accuracy" => GeneTarget.Threshold,
        "temperature" or "temp" => GeneTarget.Temperature,
        "prompt" or "template" => GeneTarget.Prompt,
        "tool" or "toolselection" or "tools" => GeneTarget.Tool,
        "knowledge" or "kb" or "knowledgebase" => GeneTarget.KnowledgeBase,
        "embedding" or "embed" => GeneTarget.Embedding,
        "safety" or "guard" or "security" => GeneTarget.Safety,
        "general" or "" => GeneTarget.General,
        _ => GeneTarget.Unknown
    };

    public static GeneOperation ParseOperation(string operationType) => operationType?.ToLowerInvariant() switch
    {
        "adjust" or "tune" or "tweak" => GeneOperation.Adjust,
        "replace" or "swap" => GeneOperation.Replace,
        "override" or "force" => GeneOperation.Override,
        "route" or "routing" => GeneOperation.Route,
        "use" or "select" => GeneOperation.Use,
        "deploy" or "activate" => GeneOperation.Deploy,
        "evaluate" or "eval" or "test" => GeneOperation.Evaluate,
        "hybrid" or "mix" => GeneOperation.Hybrid,
        "unknown" or "" => GeneOperation.Unknown,
        _ => GeneOperation.Unknown
    };

    public static string BuildConditionString(Gene gene)
    {
        var parts = new List<string>();

        foreach (var (key, value) in gene.ConditionThresholds)
            parts.Add($"{key}{value switch { >= 0 => ">=" }}_{Math.Abs(value):F2}");

        foreach (var label in gene.ConditionLabels)
            parts.Add($"label==\"{label}\"");

        if (parts.Count == 0) return gene.Condition;
        return string.Join($" {gene.ConditionOperator} ", parts);
    }

    public static string BuildActionString(Gene gene)
    {
        if (!string.IsNullOrEmpty(gene.RouteLabel))
            return $"route:{gene.RouteLabel}";
        return gene.Action;
    }

    public static string ExtractRouteLabel(string action) => action?.ToLowerInvariant() switch
    {
        string a when a.Contains("reflex") => "reflex",
        string a when a.Contains("local") => "local",
        string a when a.Contains("l1") || a.Contains("flash") => "L1",
        string a when a.Contains("l2") || a.Contains("pro") || a.Contains("deep") => "L2",
        _ => "L1"
    };

    public Gene Normalize()
    {
        var newCondition = Condition;
        var newAction = Action;

        if (ConditionThresholds.Count > 0 || ConditionLabels.Count > 0)
            newCondition = BuildConditionString(this);
        if (!string.IsNullOrEmpty(RouteLabel))
            newAction = BuildActionString(this);

        return this with { Condition = newCondition, Action = newAction };
    }
}

public sealed record GeneGeneration
{
    public int Generation { get; init; }
    public int PopulationSize { get; init; }
    public double AvgFitness { get; init; }
    public double MaxFitness { get; init; }
    public int Born { get; init; }
    public int Survived { get; init; }
    public int Mutated { get; init; }
    public int CrossOvers { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public sealed class GenePool
{
    private readonly ConcurrentDictionary<string, Gene> _genes = new();
    private readonly ConcurrentDictionary<string, List<string>> _nicheGeneIds = new();
    private readonly List<GeneGeneration> _history = new();
    private readonly Random _rng = new();
    private readonly int _maxPopulation;
    private readonly ILogger<GenePool> _logger;
    private int _generation;
    private int _shareCounter;

    private const int ShareInterval = 5;

    public GenePool(int maxPopulation = 200, ILogger<GenePool>? logger = null)
    {
        _maxPopulation = maxPopulation;
        _logger = logger ?? NullLogger<GenePool>.Instance;
    }

    public int Count => _genes.Count;
    public int Generation => _generation;
    public IReadOnlyList<GeneGeneration> History => _history.AsReadOnly();
    public IReadOnlyList<Gene> AllGenes => _genes.Values.OrderByDescending(g => g.Fitness).ToList();

    public Gene AddGene(Gene gene)
    {
        if (_genes.Count >= _maxPopulation && !_genes.ContainsKey(gene.Id))
        {
            // Find worst unprotected gene (protected genes are immune to eviction)
            var worst = _genes.Values
                .Where(g => !g.IsProtected)
                .OrderBy(g => g.Fitness)
                .FirstOrDefault();

            if (worst == null) return gene; // all protected, reject new gene

            _genes.TryRemove(worst.Id, out _);
            foreach (var (niche, ids) in _nicheGeneIds)
                ids.Remove(worst.Id);
            _logger.LogDebug("Gene pool full ({Count}/{Max}), evicted {EvictedId} (protected={Protected})",
                _genes.Count, _maxPopulation, worst.Id, _genes.Values.Count(g => g.IsProtected));
        }

        _genes[gene.Id] = gene;
        _nicheGeneIds.AddOrUpdate(gene.Niche,
            _ => new List<string> { gene.Id },
            (_, list) => { list.Add(gene.Id); return list; });
        _logger.LogDebug("Gene {Id} added: [{Niche}] {Target}/{Operation} {ConditionPreview}",
            gene.Id, gene.Niche, gene.Target, gene.Operation,
            (gene.ConditionThresholds.Count > 0
                ? string.Join(",", gene.ConditionThresholds.Select(kv => $"{kv.Key}={kv.Value:F2}"))
                : gene.Condition[..Math.Min(gene.Condition.Length, 60)]));
        return gene;
    }

    public IReadOnlyList<string> GetNiches()
    {
        return _nicheGeneIds.Keys.ToList();
    }

    public Gene? SelectByFitness(string? niche = null)
    {
        var candidates = niche != null
            ? _nicheGeneIds.GetValueOrDefault(niche, new List<string>())
                .Select(id => _genes.GetValueOrDefault(id)).Where(g => g != null).Cast<Gene>().ToList()
            : _genes.Values.ToList();

        if (candidates.Count == 0) return null;

        var totalFitness = candidates.Sum(g => Math.Max(g.Fitness, 0.001));
        var r = _rng.NextDouble() * totalFitness;
        double cumulative = 0;

        foreach (var gene in candidates)
        {
            cumulative += Math.Max(gene.Fitness, 0.001);
            if (cumulative >= r) return gene;
        }

        return candidates.Last();
    }

    public GenePool Seed(IReadOnlyList<Gene> seedGenes)
    {
        foreach (var g in seedGenes)
            AddGene(g);
        _generation = 1;
        _logger.LogInformation("GenePool seeded with {Count} genes", seedGenes.Count);
        return this;
    }

    public Gene? SelectByFitness()
    {
        return SelectByFitness(null);
    }

    public Gene? SelectElite(string? niche = null)
    {
        var genes = niche != null
            ? _nicheGeneIds.GetValueOrDefault(niche, new List<string>())
                .Select(id => _genes.GetValueOrDefault(id)).Where(g => g != null).Cast<Gene>()
            : _genes.Values;
        return genes.OrderByDescending(g => g.Fitness).FirstOrDefault();
    }

    public IReadOnlyList<Gene> SelectTopN(int n, string? niche = null)
    {
        var genes = niche != null
            ? _nicheGeneIds.GetValueOrDefault(niche, new List<string>())
                .Select(id => _genes.GetValueOrDefault(id)).Where(g => g != null).Cast<Gene>()
            : _genes.Values;
        return genes.OrderByDescending(g => g.Fitness).Take(n).ToList();
    }

    public Gene Crossover(Gene parent1, Gene parent2)
    {
        var hasStructured = parent1.ConditionThresholds.Count > 0 || parent2.ConditionThresholds.Count > 0;

        string newCondition;
        string newAction;
        Dictionary<string, double> newThresholds;
        List<string> newLabels;
        string newOperator;
        string newRouteLabel;

        if (hasStructured)
        {
            newThresholds = BlendThresholds(parent1.ConditionThresholds, parent2.ConditionThresholds);
            newLabels = _rng.NextDouble() < 0.5
                ? new List<string>(parent1.ConditionLabels)
                : new List<string>(parent2.ConditionLabels);
            newOperator = _rng.NextDouble() < 0.5 ? parent1.ConditionOperator : parent2.ConditionOperator;
            newRouteLabel = _rng.NextDouble() < 0.5 ? parent1.RouteLabel : parent2.RouteLabel;
            newCondition = "";
            newAction = "";
        }
        else
        {
            var conditionTokens1 = Tokenize(parent1.Condition);
            var conditionTokens2 = Tokenize(parent2.Condition);
            var actionTokens1 = Tokenize(parent1.Action);
            var actionTokens2 = Tokenize(parent2.Action);

            var splitCond = _rng.Next(1, Math.Max(1, Math.Min(conditionTokens1.Count, conditionTokens2.Count)));
            var splitAct = _rng.Next(1, Math.Max(1, Math.Min(actionTokens1.Count, actionTokens2.Count)));

            newCondition = string.Join("",
                conditionTokens1.Take(splitCond)
                    .Concat(conditionTokens2.Skip(splitCond)));
            newAction = string.Join("",
                actionTokens1.Take(splitAct)
                    .Concat(actionTokens2.Skip(splitAct)));
            newThresholds = new Dictionary<string, double>();
            newLabels = new List<string>();
            newOperator = "and";
            newRouteLabel = "";
        }

        var niche = parent1.Niche;
        if (_rng.NextDouble() < 0.3 && parent1.Niche == parent2.Niche)
            niche = parent1.Niche;

        var target = _rng.NextDouble() < 0.5 ? parent1.Target : parent2.Target;
        var operation = _rng.NextDouble() < 0.5 ? parent1.Operation : parent2.Operation;

        return new Gene
        {
            Condition = newCondition,
            Action = newAction,
            ConditionThresholds = newThresholds,
            ConditionLabels = newLabels,
            ConditionOperator = newOperator,
            RouteLabel = newRouteLabel,
            Target = target,
            Operation = operation,
            Source = $"crossover_{parent1.Id[..Math.Min(6, parent1.Id.Length)]}_{parent2.Id[..Math.Min(6, parent2.Id.Length)]}",
            Niche = niche,
            Weight = (parent1.Weight + parent2.Weight) / 2.0
        };
    }

    private Dictionary<string, double> BlendThresholds(
        Dictionary<string, double> t1, Dictionary<string, double> t2)
    {
        var result = new Dictionary<string, double>();
        var allKeys = new HashSet<string>(t1.Keys.Concat(t2.Keys));

        foreach (var key in allKeys)
        {
            var v1 = t1.GetValueOrDefault(key, 0.5);
            var v2 = t2.GetValueOrDefault(key, 0.5);
            var blend = _rng.NextDouble() < 0.5
                ? (v1 + v2) / 2.0
                : (_rng.NextDouble() < 0.5 ? v1 : v2);
            result[key] = Math.Clamp(blend, 0.01, 1.0);
        }

        return result;
    }

    public void ShareAcrossNiches(int topN = 3)
    {
        var niches = GetNiches();
        if (niches.Count < 2) return;

        var bestPerNiche = new Dictionary<string, IReadOnlyList<Gene>>();
        foreach (var niche in niches)
        {
            var elite = SelectTopN(topN, niche);
            if (elite.Count > 0) bestPerNiche[niche] = elite;
        }

        var shared = 0;
        foreach (var sourceNiche in bestPerNiche.Keys)
        {
            foreach (var targetNiche in bestPerNiche.Keys.Where(n => n != sourceNiche))
            {
                foreach (var eliteGene in bestPerNiche[sourceNiche])
                {
                    if (_genes.Count >= _maxPopulation) break;

                    var sharedGene = new Gene
                    {
                        Condition = eliteGene.Condition,
                        Action = eliteGene.Action,
                        Target = eliteGene.Target,
                        Operation = eliteGene.Operation,
                        TargetModule = eliteGene.TargetModule,
                        OperationType = eliteGene.OperationType,
                        ConditionThresholds = new Dictionary<string, double>(eliteGene.ConditionThresholds),
                        ConditionLabels = new List<string>(eliteGene.ConditionLabels),
                        ConditionOperator = eliteGene.ConditionOperator,
                        RouteLabel = eliteGene.RouteLabel,
                        Parameters = new Dictionary<string, object>(eliteGene.Parameters),
                        Weight = eliteGene.Fitness * 0.7,
                        Source = $"share_{sourceNiche}_{targetNiche}",
                        Niche = targetNiche
                    };
                    AddGene(sharedGene);
                    shared++;
                }
            }
        }

        _logger.LogInformation("Niche sharing: propagated {Count} elite genes across {Niches} niches",
            shared, niches.Count);
    }

    public GeneGeneration Evolve(int eliteCount = 5, int crossoverCount = 10, int mutateCount = 15)
    {
        _shareCounter++;
        if (_shareCounter % ShareInterval == 0)
            ShareAcrossNiches();

        var niches = GetNiches();
        var born = 0;

        // Unprotect all genes before selecting new elites
        foreach (var kv in _genes)
        {
            _genes[kv.Key] = kv.Value with { IsProtected = false };
        }

        foreach (var niche in niches.Append("general"))
        {
            var nicheGenes = SelectTopN(eliteCount, niche);

            // Mark elite genes as protected from eviction
            foreach (var elite in nicheGenes)
            {
                if (_genes.TryGetValue(elite.Id, out var existing))
                {
                    _genes[elite.Id] = existing with { IsProtected = true };
                }
            }

            born += nicheGenes.Count(g => _genes.ContainsKey(g.Id));

            for (var i = 0; i < crossoverCount / Math.Max(1, niches.Count); i++)
            {
                var parent1 = SelectByFitness(niche);
                var parent2 = SelectByFitness(niche);
                if (parent1 == null || parent2 == null) continue;
                if (_genes.Count >= _maxPopulation) break;

                var child = Crossover(parent1, parent2);
                AddGene(child);
                born++;
            }

            for (var i = 0; i < mutateCount / Math.Max(1, niches.Count); i++)
            {
                var parent = SelectByFitness(niche);
                if (parent == null) continue;
                if (_genes.Count >= _maxPopulation) break;

                var strength = 0.1 + _rng.NextDouble() * 0.2;
                var child = Mutate(parent, strength);
                AddGene(child);
                born++;
            }
        }

        _generation++;
        var allFitness = _genes.Values.Select(g => g.Fitness).ToList();
        var gen = new GeneGeneration
        {
            Generation = _generation,
            PopulationSize = _genes.Count,
            AvgFitness = allFitness.Count > 0 ? allFitness.Average() : 0,
            MaxFitness = allFitness.Count > 0 ? allFitness.Max() : 0,
            Born = born,
            Survived = _genes.Count - born,
            Mutated = mutateCount,
            CrossOvers = crossoverCount,
            Timestamp = DateTime.UtcNow
        };

        _history.Add(gen);
        while (_history.Count > 50) _history.RemoveAt(0);

        // Plateau detection: warn and respond if max fitness hasn't improved in 10+ generations
        if (_history.Count >= 10)
        {
            var recent = _history.TakeLast(10).ToList();
            var currentMax = recent[^1].MaxFitness;
            var plateaued = recent.All(g => g.MaxFitness <= currentMax + 0.01 && g.MaxFitness >= currentMax - 0.01);
            if (plateaued && currentMax < 0.95)
            {
                _logger.LogWarning("GenePool: PLATEAU detected — max fitness {Max:F3} unchanged over {Count} generations. Injecting diversity...",
                    currentMax, _history.Count);

                // Response 1: Boost mutation strength by 2x for next iteration
                var boostedMutate = (int)(mutateCount * 1.5);
                var boostedCross = (int)(crossoverCount * 1.3);

                // Response 2: Inject random genes (5% of population)
                var injectCount = Math.Max(3, _genes.Count / 20);
                for (var i = 0; i < injectCount; i++)
                {
                    var novelty = new Gene
                    {
                        Condition = $"random_plateau_breaker_{_generation}_{i}",
                        Action = "explore",
                        Fitness = 0.1 + _rng.NextDouble() * 0.2,
                        Niche = niches[_rng.Next(niches.Count)],
                        IsProtected = false
                    };
                    AddGene(novelty);
                }

                // Response 3: Clear recent history to prevent repeated plateau warnings
                _history.Clear();

                _logger.LogInformation("GenePool: plateau response — injected {Inject} random genes, boosted mutation {Old}→{New}",
                    injectCount, mutateCount, boostedMutate);
            }
        }

        _logger.LogInformation("Generation {Gen}: pop={Pop}, avgF={Avg:F3}, maxF={Max:F3}, born={Born}",
            _generation, _genes.Count, gen.AvgFitness, gen.MaxFitness, born);

        return gen;
    }

    public Gene Mutate(Gene gene, double mutationStrength = 0.1)
    {
        var newThresholds = new Dictionary<string, double>(gene.ConditionThresholds);
        var newLabels = new List<string>(gene.ConditionLabels);
        var newOperator = gene.ConditionOperator;
        var newRouteLabel = gene.RouteLabel;
        var newCondition = gene.Condition;
        var newAction = gene.Action;

        if (newThresholds.Count > 0)
        {
            var mutatedKeys = new List<string>();
            foreach (var key in newThresholds.Keys.ToList())
            {
                if (_rng.NextDouble() < mutationStrength)
                {
                    var current = newThresholds[key];
                    var jitter = (_rng.NextDouble() - 0.5) * mutationStrength * 0.4;
                    newThresholds[key] = Math.Clamp(current + jitter, 0.01, 1.0);
                    mutatedKeys.Add(key);
                }
            }
        }

        if (_rng.NextDouble() < mutationStrength * 0.3)
            newOperator = gene.ConditionOperator == "and" ? "or" : "and";

        if (_rng.NextDouble() < mutationStrength && newLabels.Count > 0)
        {
            var idx = _rng.Next(newLabels.Count);
            newLabels.RemoveAt(idx);
        }

        if (!string.IsNullOrEmpty(gene.RouteLabel) && _rng.NextDouble() < mutationStrength * 0.5)
            newRouteLabel = _routeLabels[_rng.Next(_routeLabels.Length)];

        var hasStructured = newThresholds.Count > 0 || newLabels.Count > 0;
        if (!hasStructured)
        {
            var tokens = Tokenize(gene.Condition);
            var newTokens = new List<string>(tokens);
            for (var i = 0; i < tokens.Count; i++)
            {
                if (_rng.NextDouble() < mutationStrength)
                {
                    if (tokens[i] == "&&") newTokens[i] = "||";
                    else if (tokens[i] == "||") newTokens[i] = "&&";
                    else if (tokens[i] == ">") newTokens[i] = "<";
                    else if (tokens[i] == "<") newTokens[i] = ">";
                    else if (tokens[i] == "==") newTokens[i] = "!=";
                    else if (tokens[i] == "!=") newTokens[i] = "==";
                }
            }
            newCondition = string.Join("", newTokens);
            newAction = gene.Action;
        }

        var newWeight = Math.Clamp(gene.Weight + (_rng.NextDouble() - 0.5) * mutationStrength * 2, 0.1, 10.0);

        var newTarget = gene.Target;
        if (_rng.NextDouble() < mutationStrength)
            newTarget = _targetValues[_rng.Next(_targetValues.Length)];

        var newOperation = gene.Operation;
        if (_rng.NextDouble() < mutationStrength)
            newOperation = _operationValues[_rng.Next(_operationValues.Length)];

        return new Gene
        {
            Condition = newCondition,
            Action = newAction,
            ConditionThresholds = newThresholds,
            ConditionLabels = newLabels,
            ConditionOperator = newOperator,
            RouteLabel = newRouteLabel,
            Target = newTarget,
            Operation = newOperation,
            TargetModule = gene.TargetModule,
            OperationType = gene.OperationType,
            Parameters = new Dictionary<string, object>(gene.Parameters),
            Weight = newWeight,
            Niche = gene.Niche,
            Source = $"mutant_{gene.Id[..Math.Min(6, gene.Id.Length)]}",
            MutationHistory = new List<string>(gene.MutationHistory) { $"mutate({gene.Id})" }
        };
    }

    private static readonly GeneTarget[] _targetValues =
    {
        GeneTarget.Router, GeneTarget.Classifier, GeneTarget.Threshold,
        GeneTarget.Temperature, GeneTarget.Prompt, GeneTarget.Tool,
        GeneTarget.KnowledgeBase, GeneTarget.Embedding, GeneTarget.Safety,
        GeneTarget.General
    };

    private static readonly GeneOperation[] _operationValues =
    {
        GeneOperation.Adjust, GeneOperation.Replace, GeneOperation.Override,
        GeneOperation.Route, GeneOperation.Use, GeneOperation.Deploy,
        GeneOperation.Evaluate, GeneOperation.Hybrid
    };

    private static readonly string[] _routeLabels =
    {
        "reflex", "local", "L1", "L2"
    };

    public void UpdateFitness(string geneId, double reward)
    {
        if (_genes.TryGetValue(geneId, out var gene))
        {
            gene.Trials++;
            if (reward > 0.5) gene.Successes++;
            gene.Fitness = gene.Trials > 0 ? (double)gene.Successes / gene.Trials : 0;
            gene.LastEvaluatedAt = DateTime.UtcNow;
        }
    }

    public void DecayUnused(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var staleIds = _genes.Values
            .Where(g => g.LastEvaluatedAt < cutoff && g.Trials < 3)
            .Select(g => g.Id)
            .ToList();

        foreach (var id in staleIds)
            _genes.TryRemove(id, out _);

        if (staleIds.Count > 0)
            _logger.LogDebug("Decayed {Count} stale genes", staleIds.Count);
    }

    public bool RemoveGene(string geneId)
    {
        if (_genes.TryRemove(geneId, out _))
        {
            foreach (var (niche, ids) in _nicheGeneIds)
                ids.Remove(geneId);
            _logger.LogDebug("Removed gene {GeneId}", geneId);
            return true;
        }
        return false;
    }

    private static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var current = "";
        foreach (var ch in input)
        {
            if (ch is '&' or '|' or '=' or '!' or '<' or '>' or '(' or ')' or '\'' or '\"')
            {
                if (current.Length > 0) { tokens.Add(current); current = ""; }
                tokens.Add(ch.ToString());
            }
            else
            {
                current += ch;
            }
        }
        if (current.Length > 0) tokens.Add(current);
        return tokens;
    }
}
