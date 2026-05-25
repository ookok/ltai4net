using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LTAI.Tools.Reasoning;

public sealed class FormalLogicEngine
{
    private readonly ILogger<FormalLogicEngine> _logger;
    private readonly Dictionary<string, HashSet<string>> _facts = new();
    private readonly List<LogicRule> _rules = new();

    public FormalLogicEngine(ILogger<FormalLogicEngine> logger)
    {
        _logger = logger;
        InitializeBuiltInRules();
    }

    public void AddFact(string category, string fact)
    {
        if (!_facts.ContainsKey(category))
            _facts[category] = new HashSet<string>();
        _facts[category].Add(fact);
        _logger.LogDebug("Fact added: {Category} -> {Fact}", category, fact);
    }

    public void AddRule(LogicRule rule)
    {
        _rules.Add(rule);
        _logger.LogDebug("Rule added: {Name}", rule.Name);
    }

    public async Task<LogicResult> ReasonAsync(
        string query,
        ReasoningMode mode = ReasoningMode.Forward,
        CancellationToken cancellationToken = default)
    {
        var result = new LogicResult { Query = query };

        try
        {
            if (mode == ReasoningMode.Forward)
                result = ForwardChain(query);
            else if (mode == ReasoningMode.Backward)
                result = BackwardChain(query);
            else
                result = EvaluateConstraint(query);
        }
        catch (Exception ex)
        {
            result.Conclusion = $"Reasoning error: {ex.Message}";
            result.Confidence = 0;
        }

        return await Task.FromResult(result).ConfigureAwait(false);
    }

    private LogicResult ForwardChain(string query)
    {
        var derived = new HashSet<string>();
        var applied = true;
        var steps = new List<string>();
        var iteration = 0;

        while (applied && iteration < 50)
        {
            applied = false;
            iteration++;

            foreach (var rule in _rules)
            {
                if (rule.IsSatisfied(_facts) && derived.Add(rule.Conclusion))
                {
                    steps.Add($"[{iteration}] {rule.Name}: {string.Join(" + ", rule.Premises)} → {rule.Conclusion} (confidence: {rule.Confidence})");
                    AddFact("derived", rule.Conclusion);
                    applied = true;
                }
            }
        }

        var queryFact = _facts.SelectMany(kvp => kvp.Value).FirstOrDefault(f => f.Contains(query, StringComparison.OrdinalIgnoreCase));

        return new LogicResult
        {
            Query = query,
            Conclusion = queryFact ?? "No conclusion reached",
            Confidence = queryFact != null ? 0.8 : 0.1,
            Mode = "forward",
            Steps = steps,
            FactsCount = _facts.Sum(kvp => kvp.Value.Count),
            RulesCount = _rules.Count
        };
    }

    private LogicResult BackwardChain(string goal)
    {
        var visited = new HashSet<string>();
        var steps = new List<string>();

        bool Prove(string g, int depth)
        {
            if (depth > 20) return false;
            if (visited.Contains(g)) return false;
            visited.Add(g);

            foreach (var (cat, facts) in _facts)
            {
                if (facts.Contains(g))
                {
                    steps.Add($"[depth={depth}] Found fact: {g} (in {cat})");
                    return true;
                }
            }

            foreach (var rule in _rules)
            {
                if (rule.Conclusion == g)
                {
                    steps.Add($"[depth={depth}] Trying rule: {rule.Name} → {g}");
                    var allPremisesTrue = rule.Premises.All(p => Prove(p, depth + 1));
                    if (allPremisesTrue)
                    {
                        steps.Add($"[depth={depth}] Proved: {g} via {rule.Name}");
                        return true;
                    }
                }
            }

            steps.Add($"[depth={depth}] Cannot prove: {g}");
            return false;
        }

        var proved = Prove(goal, 0);

        return new LogicResult
        {
            Query = goal,
            Conclusion = proved ? $"'{goal}' is TRUE" : $"'{goal}' could not be proven",
            Confidence = proved ? 0.9 : 0.2,
            Mode = "backward",
            Steps = steps
        };
    }

    private LogicResult EvaluateConstraint(string query)
    {
        var steps = new List<string>();
        double confidence = 0.5;

        var ifMatch = Regex.Match(query, @"if\s+(.+?)\s+then\s+(.+)", RegexOptions.IgnoreCase);
        if (ifMatch.Success)
        {
            var condition = ifMatch.Groups[1].Value;
            var conclusion = ifMatch.Groups[2].Value;
            var isTrue = EvaluateBoolean(condition);

            steps.Add($"Condition: {condition} → {isTrue}");
            steps.Add(isTrue ? $"Therefore: {conclusion}" : $"Condition not met, conclusion: {conclusion} cannot be asserted");
            confidence = isTrue ? 0.85 : 0.15;
        }
        else
        {
            steps.Add($"Constraint check: {query}");
            var hasRule = _rules.Any(r => r.Conclusion.Contains(query, StringComparison.OrdinalIgnoreCase));
            steps.Add(hasRule ? "Matching rule found" : "No matching rule");
            confidence = hasRule ? 0.7 : 0.3;
        }

        return new LogicResult
        {
            Query = query,
            Conclusion = confidence > 0.5 ? "Constraint satisfied" : "Constraint not satisfied",
            Confidence = confidence,
            Mode = "constraint",
            Steps = steps
        };
    }

    private bool EvaluateBoolean(string condition)
    {
        condition = condition.Trim();

        foreach (var (cat, facts) in _facts)
        {
            foreach (var fact in facts)
            {
                if (condition.Contains(fact, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        if (condition.Contains(" and "))
        {
            var parts = condition.Split(" and ");
            return parts.All(EvaluateBoolean);
        }

        if (condition.Contains(" or "))
        {
            var parts = condition.Split(" or ");
            return parts.Any(EvaluateBoolean);
        }

        return false;
    }

    private void InitializeBuiltInRules()
    {
        AddRule(new LogicRule
        {
            Name = "modus_ponens",
            Premises = new[] { "rain", "wet_ground_if_rain" },
            Conclusion = "ground_is_wet",
            Confidence = 0.95
        });

        AddRule(new LogicRule
        {
            Name = "transitive",
            Premises = new[] { "A_implies_B", "B_implies_C" },
            Conclusion = "A_implies_C",
            Confidence = 0.9
        });

        AddRule(new LogicRule
        {
            Name = "code_has_import",
            Premises = new[] { "uses_external_library" },
            Conclusion = "has_dependencies",
            Confidence = 0.9
        });
    }
}

public sealed class LogicRule
{
    public string Name { get; init; } = "";
    public string[] Premises { get; init; } = Array.Empty<string>();
    public string Conclusion { get; init; } = "";
    public double Confidence { get; init; } = 1.0;

    public bool IsSatisfied(Dictionary<string, HashSet<string>> facts)
    {
        var allFacts = facts.SelectMany(kvp => kvp.Value).ToHashSet();
        return Premises.All(p => allFacts.Any(f => f.Contains(p, StringComparison.OrdinalIgnoreCase)));
    }
}

public sealed class LogicResult
{
    public string Query { get; init; } = "";
    public string Conclusion { get; set; } = "";
    public double Confidence { get; set; }
    public string Mode { get; set; } = "forward";
    public List<string> Steps { get; set; } = new();
    public int FactsCount { get; set; }
    public int RulesCount { get; set; }
}

public enum ReasoningMode { Forward, Backward, Constraint }
