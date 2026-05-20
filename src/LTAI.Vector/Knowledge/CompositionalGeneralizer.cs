using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LTAI.Vector.Knowledge;

public enum CompositionOperator { And, Then, Or, Not, Permute }

public sealed record ComposedRule
{
    public string Id { get; init; } = "";
    public string Premise { get; init; } = "";
    public string Conclusion { get; init; } = "";
    public List<string> SourceRuleIds { get; init; } = new();
    public CompositionOperator Operator { get; init; }
    public double Confidence { get; init; }
    public string ComposedText { get; init; } = "";
}

public sealed record NoveltyAssessment
{
    public string InputText { get; init; } = "";
    public double PremiseEmbeddingScore { get; init; }
    public int PremiseGraphDepth { get; init; }
    public double RelevancyIndex { get; init; }
    public int ApplicableRules { get; init; }
    public double EdgeDensity { get; init; }
    public double ConstraintDepth { get; init; }
    public double NoveltyScore { get; init; }
    public string Verdict { get; init; } = "";
    public List<ComposedRule> SuggestedCompositions { get; init; } = new();
}

public sealed class CompositionalGeneralizer
{
    private readonly KnowledgeGraph _graph;
    private readonly RelationEngine _relationEngine;
    private readonly ILogger<CompositionalGeneralizer>? _logger;

    private readonly List<ComposedRule> _composedRules = new();
    private readonly Dictionary<string, double> _ruleEffectiveness = new();
    private readonly object _lock = new();
    private const int MaxComposedRules = 300;

    public CompositionalGeneralizer(
        KnowledgeGraph graph,
        RelationEngine relationEngine,
        ILogger<CompositionalGeneralizer>? logger = null)
    {
        _graph = graph;
        _relationEngine = relationEngine;
        _logger = logger;
    }

    public List<ComposedRule> ComposeRules(List<string> premises, List<string> conclusions,
        CompositionOperator op, double minConfidence = 0.3)
    {
        var composed = new List<ComposedRule>();

        switch (op)
        {
            case CompositionOperator.And:
                composed = ComposeAnd(premises, conclusions, minConfidence);
                break;
            case CompositionOperator.Then:
                composed = ComposeThen(premises, conclusions, minConfidence);
                break;
            case CompositionOperator.Or:
                composed = ComposeOr(premises, conclusions, minConfidence);
                break;
            case CompositionOperator.Not:
                composed = ComposeNot(premises, conclusions, minConfidence);
                break;
            case CompositionOperator.Permute:
                composed = ComposePermute(premises, conclusions, minConfidence);
                break;
        }

        lock (_lock)
        {
            _composedRules.AddRange(composed);
            while (_composedRules.Count > MaxComposedRules)
                _composedRules.RemoveAt(0);
        }

        _logger?.LogInformation(
            "CompositionalGeneralizer: composed {Count} rules with op={Op}",
            composed.Count, op);

        return composed;
    }

    private List<ComposedRule> ComposeAnd(List<string> premises, List<string> conclusions, double minConfidence)
    {
        var result = new List<ComposedRule>();

        for (int i = 0; i < premises.Count; i++)
        {
            for (int j = 0; j < conclusions.Count; j++)
            {
                var prem = Normalize(premises[i]);
                var conc = Normalize(conclusions[j]);

                if (prem.Length < 3 || conc.Length < 3) continue;
                if (prem.Equals(conc, StringComparison.OrdinalIgnoreCase)) continue;

                var overlap = WordOverlap(prem, conc);
                var confidence = 0.3 + overlap * 0.5;
                if (confidence < minConfidence) continue;

                result.Add(new ComposedRule
                {
                    Id = $"cand_{Guid.NewGuid():N}"[..14],
                    Premise = prem,
                    Conclusion = conc,
                    Operator = CompositionOperator.And,
                    Confidence = confidence,
                    ComposedText = $"{prem} AND {conc}"
                });
            }
        }

        return result;
    }

    private List<ComposedRule> ComposeThen(List<string> premises, List<string> conclusions, double minConfidence)
    {
        var result = new List<ComposedRule>();

        foreach (var prem in premises)
        {
            foreach (var conc in conclusions)
            {
                var p = Normalize(prem);
                var c = Normalize(conc);

                if (p.Length < 3 || c.Length < 3) continue;
                if (p.Equals(c, StringComparison.OrdinalIgnoreCase)) continue;

                var hasCausalLink = HasCausalRelation(p, c);
                var pathExists = _graph.FindPath(p, c).Count > 0;
                var confidence = hasCausalLink ? 0.6 : pathExists ? 0.45 : 0.25 + WordOverlap(p, c) * 0.2;

                if (confidence < minConfidence) continue;

                result.Add(new ComposedRule
                {
                    Id = $"cthen_{Guid.NewGuid():N}"[..14],
                    Premise = p,
                    Conclusion = c,
                    Operator = CompositionOperator.Then,
                    Confidence = confidence,
                    ComposedText = $"IF {p} THEN {c}"
                });
            }
        }

        return result;
    }

    private List<ComposedRule> ComposeOr(List<string> premises, List<string> conclusions, double minConfidence)
    {
        var result = new List<ComposedRule>();

        for (int i = 0; i < premises.Count - 1; i++)
        {
            for (int j = i + 1; j < premises.Count; j++)
            {
                var p1 = Normalize(premises[i]);
                var p2 = Normalize(premises[j]);

                if (p1.Length < 3 || p2.Length < 3) continue;
                if (p1.Equals(p2, StringComparison.OrdinalIgnoreCase)) continue;

                var overlap = WordOverlap(p1, p2);
                var confidence = overlap > 0.3 ? 0.5 : 0.25;
                if (confidence < minConfidence) continue;

                var concText = conclusions.Count > 0
                    ? Normalize(conclusions[Math.Min(j, conclusions.Count - 1)])
                    : $"{p1} OR {p2}";

                result.Add(new ComposedRule
                {
                    Id = $"cor_{Guid.NewGuid():N}"[..14],
                    Premise = $"{p1} OR {p2}",
                    Conclusion = concText,
                    Operator = CompositionOperator.Or,
                    Confidence = confidence,
                    ComposedText = $"({p1} OR {p2}) => {concText}"
                });
            }
        }

        return result;
    }

    private List<ComposedRule> ComposeNot(List<string> premises, List<string> conclusions, double minConfidence)
    {
        var result = new List<ComposedRule>();

        foreach (var prem in premises)
        {
            var negated = NegateText(Normalize(prem));
            if (string.IsNullOrEmpty(negated)) continue;

            var concText = conclusions.Count > 0
                ? Normalize(conclusions[0])
                : "opposite holds";

            result.Add(new ComposedRule
            {
                Id = $"cnot_{Guid.NewGuid():N}"[..14],
                Premise = negated,
                Conclusion = concText,
                Operator = CompositionOperator.Not,
                Confidence = 0.35,
                ComposedText = $"NOT({prem}) => {concText}"
            });
        }

        return result;
    }

    private List<ComposedRule> ComposePermute(List<string> premises, List<string> conclusions, double minConfidence)
    {
        var result = new List<ComposedRule>();

        for (int i = 0; i < premises.Count; i++)
        {
            for (int j = 0; j < conclusions.Count; j++)
            {
                var prem = Normalize(premises[i]);
                var conc = Normalize(conclusions[j]);

                for (int k = 0; k < premises.Count; k++)
                {
                    if (k == i) continue;
                    var constraint = Normalize(premises[k]);

                    if (ConstraintApplies(constraint, prem, conc))
                    {
                        var confidence = 0.35 + WordOverlap(prem, conc) * 0.3;
                        if (confidence < minConfidence) continue;

                        result.Add(new ComposedRule
                        {
                            Id = $"cperm_{Guid.NewGuid():N}"[..14],
                            Premise = prem,
                            Conclusion = conc,
                            Operator = CompositionOperator.Permute,
                            Confidence = confidence,
                            ComposedText = $"({prem} => {conc}) | constraint: {constraint}"
                        });
                    }
                }
            }
        }

        return result;
    }

    public NoveltyAssessment AssessNovelty(string input, List<string> existingPremises)
    {
        var premEmbeddingScore = existingPremises.Count > 0
            ? existingPremises.Max(p => WordOverlap(input, p))
            : 0;

        var graphDepth = _graph.FindPath(input, "").Count;

        int applicableRules = 0;
        double relevancySum = 0, edgeDensity = 0, constraintDepth = 0;

        lock (_lock)
        {
            applicableRules = _composedRules.Count(r =>
                WordOverlap(r.Premise, input) > 0.3 || WordOverlap(r.Conclusion, input) > 0.3);

            foreach (var rule in _composedRules.TakeLast(100))
            {
                var rel = Math.Max(WordOverlap(rule.Premise, input), WordOverlap(rule.Conclusion, input));
                if (rel > 0.2) relevancySum += rel;
            }

            edgeDensity = _composedRules.Count > 0
                ? (double)_composedRules.Count(r => r.Operator == CompositionOperator.Then || r.Operator == CompositionOperator.And) / _composedRules.Count
                : 0;

            constraintDepth = _composedRules.Count(r => r.Operator == CompositionOperator.Permute) / (double)Math.Max(1, _composedRules.Count);
        }

        var relevancyIndex = applicableRules > 0 ? relevancySum / applicableRules : 0;

        var noveltyScore = ComputeNoveltyScore(
            premEmbeddingScore, graphDepth, relevancyIndex, applicableRules, edgeDensity, constraintDepth);

        var suggestedComps = applicableRules < 5 && existingPremises.Count > 0
            ? ComposeRules(existingPremises.Take(3).ToList(), new() { input },
                CompositionOperator.Then, 0.2)
            : new List<ComposedRule>();

        return new NoveltyAssessment
        {
            InputText = input,
            PremiseEmbeddingScore = Math.Round(premEmbeddingScore, 3),
            PremiseGraphDepth = graphDepth,
            RelevancyIndex = Math.Round(relevancyIndex, 3),
            ApplicableRules = applicableRules,
            EdgeDensity = Math.Round(edgeDensity, 3),
            ConstraintDepth = Math.Round(constraintDepth, 3),
            NoveltyScore = Math.Round(noveltyScore, 3),
            Verdict = noveltyScore > 0.6 ? "highly_novel"
                : noveltyScore > 0.35 ? "moderately_novel"
                : noveltyScore > 0.15 ? "slightly_novel"
                : "familiar",
            SuggestedCompositions = suggestedComps
        };
    }

    public List<ComposedRule> GetComposedRules(string? opFilter = null, double minConfidence = 0.0)
    {
        lock (_lock)
        {
            return _composedRules
                .Where(r => opFilter == null || r.Operator.ToString().Equals(opFilter, StringComparison.OrdinalIgnoreCase))
                .Where(r => r.Confidence >= minConfidence)
                .ToList();
        }
    }

    public void RecordRuleEffectiveness(string ruleId, double effectiveness)
    {
        lock (_lock)
        {
            _ruleEffectiveness.TryGetValue(ruleId, out var old);
            _ruleEffectiveness[ruleId] = old * 0.8 + effectiveness * 0.2;
        }
    }

    public Dictionary<string, object> GetStats()
    {
        lock (_lock)
        {
            return new()
            {
                ["composed_rules"] = _composedRules.Count,
                ["by_operator"] = _composedRules.GroupBy(r => r.Operator.ToString())
                    .ToDictionary(g => g.Key, g => g.Count()),
                ["avg_confidence"] = Math.Round(
                    _composedRules.DefaultIfEmpty(new ComposedRule()).Average(r => r.Confidence), 3),
                ["avg_effectiveness"] = Math.Round(
                    _ruleEffectiveness.Values.DefaultIfEmpty(0.5).Average(), 3)
            };
        }
    }

    private static double ComputeNoveltyScore(
        double premEmbeddingScore, int graphDepth, double relevancyIndex,
        int applicableRules, double edgeDensity, double constraintDepth)
    {
        double score = 0;

        score += (1.0 - premEmbeddingScore) * 0.30;

        score += (graphDepth == 0 ? 0.2 : Math.Min(0.2, 1.0 / (graphDepth + 1))) * 0.15;

        score += (1.0 - Math.Min(1.0, relevancyIndex)) * 0.20;

        score += (applicableRules < 3 ? 0.2 : 0.05) * 0.15;

        score += edgeDensity * 0.10;

        score += constraintDepth * 0.10;

        return Math.Min(1.0, score);
    }

    private static string Normalize(string text) =>
        Regex.Replace(text.Trim(), @"\s+", " ").ToLowerInvariant();

    private static bool HasCausalRelation(string premise, string conclusion)
    {
        var causalMarkers = new[] { "所以", "因此", "导致", "引起", "造成", "使得",
            "therefore", "thus", "hence", "causes", "leads to", "results in" };
        return causalMarkers.Any(m =>
            premise.Contains(m, StringComparison.OrdinalIgnoreCase) ||
            conclusion.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private static string? NegateText(string text)
    {
        if (text.StartsWith("不")) return text[1..];
        if (text.StartsWith("not ", StringComparison.OrdinalIgnoreCase))
            return text[4..];
        if (text.Length > 2)
            return "不" + text;
        return null;
    }

    private static bool ConstraintApplies(string constraint, string premise, string conclusion)
    {
        var constraintWords = new HashSet<string>(constraint.Split(' '));
        var premWords = new HashSet<string>(premise.Split(' '));
        var concWords = new HashSet<string>(conclusion.Split(' '));

        var constraintOverlapPrem = constraintWords.Intersect(premWords).Count();
        var constraintOverlapConc = constraintWords.Intersect(concWords).Count();

        return constraintOverlapPrem >= 1 || constraintOverlapConc >= 1;
    }

    private static double WordOverlap(string a, string b)
    {
        var wa = new HashSet<string>(a.Split(new[] { ' ', '_', '-' },
            StringSplitOptions.RemoveEmptyEntries));
        var wb = new HashSet<string>(b.Split(new[] { ' ', '_', '-' },
            StringSplitOptions.RemoveEmptyEntries));

        if (wa.Count == 0 || wb.Count == 0) return 0;
        var intersect = wa.Intersect(wb).Count();
        return (double)intersect / (wa.Count + wb.Count - intersect);
    }
}
