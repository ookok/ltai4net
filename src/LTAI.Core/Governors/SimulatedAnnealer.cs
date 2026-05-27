using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Governors;

public sealed record AnnealStep
{
    public int Epoch { get; init; }
    public double Temperature { get; init; }
    public int ProposalsGenerated { get; init; }
    public int ProposalsAccepted { get; init; }
    public double AvgImprovement { get; init; }
    public string? BestNewGene { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public sealed class SimulatedAnnealer
{
    private readonly GenePool _genePool;
    private readonly ParetoRouter _paretoRouter;
    private readonly Func<string, CancellationToken, Task<string>>? _l1Eval;
    private readonly Random _rng = new();
    private readonly List<AnnealStep> _history = new();
    private readonly ILogger<SimulatedAnnealer> _logger;
    private double _temperature = 1.0;
    private int _epoch;

    public SimulatedAnnealer(
        GenePool genePool,
        ParetoRouter paretoRouter,
        Func<string, CancellationToken, Task<string>>? l1Eval = null,
        ILogger<SimulatedAnnealer>? logger = null)
    {
        _genePool = genePool;
        _paretoRouter = paretoRouter;
        _l1Eval = l1Eval;
        _logger = logger ?? NullLogger<SimulatedAnnealer>.Instance;
    }

    public double Temperature => _temperature;
    public int Epoch => _epoch;
    public IReadOnlyList<AnnealStep> History => _history.AsReadOnly();

    public async Task<AnnealStep> StepAsync(int proposalsPerEpoch = 10, CancellationToken ct = default)
    {
        _epoch++;
        var accepted = 0;
        double totalImprovement = 0;
        string? bestGeneId = null;
        double bestFitness = 0;

        for (var i = 0; i < proposalsPerEpoch; i++)
        {
            if (ct.IsCancellationRequested) break;

            var parent = _genePool.SelectByFitness();
            if (parent == null) continue;

            var strength = _temperature * (0.05 + _rng.NextDouble() * 0.15);
            var candidate = _genePool.Mutate(parent, strength);

            var candidateFitness = await EvaluateCandidateAsync(candidate, ct).ConfigureAwait(false);

            var parentFitness = parent.Fitness;
            var improvement = candidateFitness - parentFitness;

            var acceptProbability = improvement > 0
                ? 1.0
                : Math.Exp(improvement / Math.Max(_temperature, 0.001));

            if (_rng.NextDouble() < acceptProbability)
            {
                candidate.Fitness = candidateFitness;
                _genePool.AddGene(candidate);
                accepted++;
                totalImprovement += improvement;

                if (candidateFitness > bestFitness)
                {
                    bestFitness = candidateFitness;
                    bestGeneId = candidate.Id;
                }
            }

            if (_l1Eval != null && _rng.NextDouble() < _temperature * 0.3)
            {
                try
                {
                    var proposed = await ProposeWithL1Async(parent, ct).ConfigureAwait(false);
                    if (proposed != null)
                    {
                        proposed.Fitness = await EvaluateCandidateAsync(proposed, ct).ConfigureAwait(false);
                        _genePool.AddGene(proposed);
                        accepted++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "L1 proposal failed (non-critical)");
                }
            }
        }

        _temperature = Math.Max(0.001, _temperature * 0.95);

        var step = new AnnealStep
        {
            Epoch = _epoch,
            Temperature = _temperature,
            ProposalsGenerated = proposalsPerEpoch,
            ProposalsAccepted = accepted,
            AvgImprovement = accepted > 0 ? totalImprovement / accepted : 0,
            BestNewGene = bestGeneId
        };

        _history.Add(step);
        _logger.LogInformation("Anneal epoch {Epoch}: T={T:F4}, accepted={Accepted}/{Proposals}, avgImprovement={Improvement:F3}",
            _epoch, _temperature, accepted, proposalsPerEpoch, step.AvgImprovement);

        return step;
    }

    public void SetTemperature(double t)
    {
        _temperature = Math.Clamp(t, 0.001, 2.0);
    }

    public void CoolDown(double factor = 0.9)
    {
        _temperature = Math.Max(0.001, _temperature * factor);
    }

    private async Task<double> EvaluateCandidateAsync(Gene candidate, CancellationToken ct)
    {
        try
        {
            var points = _paretoRouter.GetFrontier();
            if (points.Count == 0) return 0.5;

            var embedding = EmbedCondition(candidate.Condition);
            var result = _paretoRouter.Decide(embedding);
            double coherenceScore = result.Confidence;

            double structuralScore = EvaluateStructuralQuality(candidate);

            double historicalScore = candidate.Trials > 0
                ? (double)candidate.Successes / candidate.Trials
                : 0.5;

            if (_l1Eval != null && candidate.Fitness > 0.4)
            {
                try
                {
                    var evalPrompt =
                        "Rate this routing rule on a scale 0.0-1.0. Consider: specificity, coverage, and correctness.\n" +
                        $"Rule: IF {candidate.Condition} THEN {candidate.Action}\n" +
                        $"Historical fitness: {candidate.Fitness:F3} ({candidate.Successes}/{candidate.Trials})\n" +
                        "Output just a number between 0.0 and 1.0.";

                    var evalResponse = await _l1Eval(evalPrompt, ct).ConfigureAwait(false);
                    if (double.TryParse(evalResponse.Trim(), out var l1Score))
                    {
                        l1Score = Math.Clamp(l1Score, 0.0, 1.0);
                        return (coherenceScore * 0.15) + (structuralScore * 0.15) +
                               (historicalScore * 0.20) + (l1Score * 0.50);
                    }
                }
                catch
                {
                }
            }

            return (coherenceScore * 0.20) + (structuralScore * 0.25) + (historicalScore * 0.55);
        }
        catch
        {
            return 0.3;
        }
    }

    private static double EvaluateStructuralQuality(Gene gene)
    {
        double score = 0.5;

        if (gene.ConditionThresholds.Count > 0)
            score += 0.2;

        if (gene.Condition.Contains("&&") || gene.Condition.Contains("||"))
            score += 0.1;
        if (gene.Condition.Contains(">") || gene.Condition.Contains("<") || gene.Condition.Contains("=="))
            score += 0.15;

        var condLen = gene.Condition.Length;
        if (condLen > 10 && condLen < 200) score += 0.1;
        if (condLen > 200) score -= 0.1;

        var hasNiche = !string.IsNullOrEmpty(gene.Niche) && gene.Niche != "general";
        if (hasNiche) score += 0.1;

        if (gene.Action.Contains("route:") || gene.Action.Contains("deploy:") || gene.Action.Contains("use:"))
            score += 0.05;

        if (!string.IsNullOrEmpty(gene.RouteLabel))
            score += 0.05;

        return Math.Clamp(score, 0.0, 1.0);
    }

    private async Task<Gene?> ProposeWithL1Async(Gene parent, CancellationToken ct)
    {
        if (_l1Eval == null) return null;

        var prompt = $"Given routing rule: IF {parent.Condition} THEN {parent.Action}\n" +
                     $"Fitness={parent.Fitness:F3}, Trials={parent.Trials}\n" +
                     $"Propose ONE improved version. Change either condition or action. Respond with just:\n" +
                     $"CONDITION: <new_condition>\nACTION: <new_action>";

        var response = await _l1Eval(prompt, ct).ConfigureAwait(false);
        var lines = response.Split('\n');

        var newCondition = parent.Condition;
        var newAction = parent.Action;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("CONDITION:", StringComparison.OrdinalIgnoreCase))
                newCondition = trimmed["CONDITION:".Length..].Trim();
            else if (trimmed.StartsWith("ACTION:", StringComparison.OrdinalIgnoreCase))
                newAction = trimmed["ACTION:".Length..].Trim();
        }

        if (newCondition == parent.Condition && newAction == parent.Action)
            return null;

        return new Gene
        {
            Condition = newCondition,
            Action = newAction,
            Target = parent.Target,
            Operation = parent.Operation,
            TargetModule = parent.TargetModule,
            OperationType = parent.OperationType,
            ConditionThresholds = new Dictionary<string, double>(parent.ConditionThresholds),
            ConditionLabels = new List<string>(parent.ConditionLabels),
            ConditionOperator = parent.ConditionOperator,
            RouteLabel = parent.RouteLabel,
            Parameters = new Dictionary<string, object>(parent.Parameters),
            Weight = parent.Weight,
            Source = $"l1_proposal_{parent.Id[..6]}",
            MutationHistory = new List<string>(parent.MutationHistory) { $"l1_proposal({parent.Id})" }
        };
    }

    private static float[] EmbedCondition(string condition)
    {
        var embedding = new float[768];
        var bytes = global::System.Text.Encoding.UTF8.GetBytes(condition);
        for (var i = 0; i < Math.Min(bytes.Length, embedding.Length); i++)
            embedding[i] = bytes[i] / 255f;
        return embedding;
    }
}

public sealed class GeneToRule
{
    private readonly GenePool _genePool;
    private readonly ParetoRouter _paretoRouter;
    private readonly L0IntentClassifier? _classifier;
    private readonly ILogger<GeneToRule> _logger;

    public GeneToRule(
        GenePool genePool,
        ParetoRouter paretoRouter,
        L0IntentClassifier? classifier = null,
        ILogger<GeneToRule>? logger = null)
    {
        _genePool = genePool;
        _paretoRouter = paretoRouter;
        _classifier = classifier;
        _logger = logger ?? NullLogger<GeneToRule>.Instance;
    }

    public async Task<int> DeployTopGenesAsync(int topN = 5, CancellationToken ct = default)
    {
        var topGenes = _genePool.SelectTopN(topN);
        if (topGenes.Count == 0) return 0;

        var deployed = 0;
        foreach (var gene in topGenes.Where(g => g.Fitness > 0.3))
        {
            var embedding = EmbedRule(gene);
            var label = MapActionToLabel(gene);

            _paretoRouter.AddFrontierPoint(new ParetoPoint
            {
                Id = $"gene_{gene.Id}",
                Label = label,
                Quality = (float)gene.Fitness,
                Speed = label switch
                {
                    "reflex" => 1.0f, "local" => 0.8f,
                    "L1" => 0.5f, "L2" => 0.15f, _ => 0.5f
                },
                Cost = label switch
                {
                    "reflex" => 0.0f, "local" => 0.05f,
                    "L1" => 0.15f, "L2" => 1.0f, _ => 0.5f
                },
                Embedding = embedding
            });

            deployed++;
        }

        if (deployed > 0)
        {
            _paretoRouter.PruneDominated();
            _logger.LogInformation("Deployed {Count} genes to ParetoRouter frontier", deployed);

            if (_classifier != null)
            {
                SyncKeywordsToClassifier(_classifier);
                await _classifier.PersistRulesAsync(ct).ConfigureAwait(false);
            }
        }

        return deployed;
    }

    public void ExtractRulesFromFrontier()
    {
        var points = _paretoRouter.GetFrontier();

        foreach (var point in points)
        {
            var gene = new Gene
            {
                Condition = $"label == \"{point.Label}\"",
                Action = $"route:{point.Label}",
                Target = GeneTarget.Router,
                Operation = GeneOperation.Route,
                TargetModule = "Router",
                OperationType = "Route",
                RouteLabel = point.Label,
                ConditionLabels = new List<string> { point.Label },
                Weight = (point.Quality + point.Speed + (1f - point.Cost)) / 3f,
                Fitness = 0.7,
                Source = $"frontier_{point.Id}",
                CreatedAt = DateTime.UtcNow
            };

            _genePool.AddGene(gene);
        }

        _logger.LogInformation("Extracted {Count} rules from Pareto frontier", points.Count);
    }

    private static float[] EmbedRule(Gene gene)
    {
        var combined = $"{gene.Condition}|{gene.Action}|{gene.Fitness:F3}";
        var embedding = new float[768];
        var bytes = global::System.Text.Encoding.UTF8.GetBytes(combined);
        for (var i = 0; i < Math.Min(bytes.Length, embedding.Length); i++)
            embedding[i] = bytes[i] / 255f;
        return embedding;
    }

    private static string MapActionToLabel(Gene gene)
    {
        if (!string.IsNullOrEmpty(gene.RouteLabel))
            return gene.RouteLabel;

        return MapActionToLabel(gene.Action);
    }

    private static string MapActionToLabel(string action)
    {
        var lower = action.ToLowerInvariant();
        if (lower.Contains("reflex")) return "reflex";
        if (lower.Contains("local")) return "local";
        if (lower.Contains("l1") || lower.Contains("flash")) return "L1";
        if (lower.Contains("l2") || lower.Contains("pro") || lower.Contains("deep")) return "L2";
        return "L1";
    }

    public void SyncKeywordsToClassifier(L0IntentClassifier classifier)
    {
        var topGenes = _genePool.SelectTopN(10);
        var byNiche = topGenes.GroupBy(g => g.Niche);

        foreach (var group in byNiche)
        {
            var niche = group.Key;
            if (string.IsNullOrEmpty(niche) || niche == "general") continue;

            var tokens = new HashSet<string>();
            foreach (var gene in group)
            {
                foreach (var token in TokenizeCondition(gene.Condition))
                {
                    if (token.Length > 2 && !StartsWithOperator(token))
                        tokens.Add(token.ToLowerInvariant());
                }
            }

            if (tokens.Count > 0)
            {
                var label = MapActionToLabel(group.First());
                classifier.HotUpdateKeywords(
                    niche,
                    tokens.ToArray(),
                    label switch { "reflex" => 0.4f, "local" => 0.6f, "L1" => 0.8f, _ => 0.9f },
                    label switch { "reflex" => 1.0f, "local" => 0.8f, "L1" => 0.5f, _ => 0.2f },
                    label switch { "reflex" => 0.0f, "local" => 0.1f, "L1" => 0.3f, _ => 0.8f });
            }
        }
    }

    private static List<string> TokenizeCondition(string condition)
    {
        var tokens = new List<string>();
        var current = "";
        foreach (var ch in condition)
        {
            if (ch is '&' or '|' or '=' or '!' or '<' or '>' or '(' or ')' or '\'' or '\"' or ' ')
            {
                if (current.Length > 0) { tokens.Add(current); current = ""; }
            }
            else
            {
                current += ch;
            }
        }
        if (current.Length > 0) tokens.Add(current);
        return tokens;
    }

    private static bool StartsWithOperator(string token) =>
        token.Length == 0 || token[0] is '&' or '|' or '=' or '!' or '<' or '>';
}
