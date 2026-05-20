using LTAI.Vector.Knowledge.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Vector.Knowledge;

public class RelationEngine
{
    private readonly KnowledgeGraph _kg;
    private readonly Dictionary<string, RelationRule> _rules = new();
    private readonly Dictionary<string, Dictionary<string, List<string>>> _graph = new();
    private readonly ILogger<RelationEngine> _logger;
    private bool _depsLoaded;
    private GeometricRelationSelector? _geometricSelector;
    private readonly Dictionary<string, List<(string Target, string Relation, double Score)>> _multiHopCache = new();

    private static readonly List<RelationRule> DefaultRules = new()
    {
        new("depends_on", true, false, false, "required_by"),
        new("specializes", true, false, false, "generalized_by"),
        new("composes_with", false, true),
        new("conflicts_with", false, true),
        new("related_to", false, true),
    };

    public RelationEngine(KnowledgeGraph kg, ILogger<RelationEngine> logger)
    {
        _kg = kg;
        _logger = logger;
        foreach (var rule in DefaultRules) RegisterRule(rule);
    }

    public void RegisterRule(RelationRule rule)
    {
        _rules[rule.Relation] = rule;
    }

    public void AddRelation(string source, string target, string relation)
    {
        _graph.TryAdd(source, new());
        _graph[source].TryAdd(relation, new());
        if (!_graph[source][relation].Contains(target))
            _graph[source][relation].Add(target);

        if (_rules.TryGetValue(relation, out var rule) && rule.Symmetric)
        {
            _graph.TryAdd(target, new());
            _graph[target].TryAdd(relation, new());
            if (!_graph[target][relation].Contains(source))
                _graph[target][relation].Add(source);
        }

        _kg.AddRelation(source, target, relation);
    }

    public List<string> Infer(string source, string? relation = null)
    {
        var results = new List<string>();
        var visited = new HashSet<string> { source };
        var queue = new Queue<(string node, int depth)>();
        queue.Enqueue((source, 0));

        while (queue.Count > 0)
        {
            var (node, depth) = queue.Dequeue();
            if (depth > 10) continue;

            if (_graph.TryGetValue(node, out var relations))
            {
                foreach (var (rel, targets) in relations)
                {
                    if (relation != null && rel != relation) continue;
                    foreach (var target in targets)
                    {
                        if (visited.Add(target))
                        {
                            results.Add(target);
                            queue.Enqueue((target, depth + 1));
                        }
                    }
                }
            }
        }
        return results;
    }

    public List<string> DeriveInverse(string source, string relation)
    {
        if (_rules.TryGetValue(relation, out var rule) && rule.Inverse != null)
            return Infer(source, rule.Inverse);
        return new();
    }

    public Dictionary<string, List<string>> GetClosure(List<string>? relations = null)
    {
        var closure = new Dictionary<string, List<string>>();
        foreach (var entity in _graph.Keys)
        {
            var reachable = new List<string>();
            foreach (var (rel, targets) in _graph.GetValueOrDefault(entity, new()))
            {
                if (_rules.TryGetValue(rel, out var rule) && rule.Transitive
                    && (relations == null || relations.Contains(rel)))
                {
                    foreach (var t in targets)
                        if (!reachable.Contains(t)) reachable.Add(t);
                }
            }
            if (reachable.Count > 0) closure[entity] = reachable;
        }
        return closure;
    }

    public Dictionary<string, object> GetStats()
    {
        int totalRels = 0;
        var byType = new Dictionary<string, int>();
        foreach (var (_, relations) in _graph)
            foreach (var (rel, targets) in relations)
            {
                totalRels += targets.Count;
                byType[rel] = byType.GetValueOrDefault(rel) + targets.Count;
            }

        var stats = new Dictionary<string, object>
        {
            ["entities"] = _graph.Count,
            ["total_relations"] = totalRels,
            ["relation_types"] = byType,
            ["geometric_enabled"] = _geometricSelector != null
        };

        if (_geometricSelector != null)
        {
            var gStats = _geometricSelector.GetStats();
            foreach (var kv in gStats)
                stats[$"geometric_{kv.Key}"] = kv.Value;
        }

        return stats;
    }

    public void EnableGeometricSelector(int embeddingDim = 64)
    {
        _geometricSelector = new GeometricRelationSelector(embeddingDim);
        foreach (var (subject, relations) in _graph)
        {
            var attrs = new Dictionary<string, double>();
            foreach (var (rel, targets) in relations)
            {
                foreach (var target in targets)
                {
                    attrs[$"{rel}:{target}"] = 1.0;
                }
            }
            if (attrs.Count > 0)
                _geometricSelector.EncodeSubject(subject, attrs);
        }
    }

    public List<string> SelectGeometric(string subject, string relation, int topK = 5)
    {
        if (_geometricSelector == null)
            EnableGeometricSelector();

        if (_geometricSelector != null)
        {
            _geometricSelector.LearnRelationGate(relation);
            return _geometricSelector.Select(subject, relation, topK);
        }

        return Infer(subject, relation);
    }

    public List<(string Target, string Relation, double Score)> MultiHopChain(
        string subject, string[] relations, bool useGeometric = true)
    {
        var cacheKey = $"{subject}|{string.Join(">", relations)}";
        if (_multiHopCache.TryGetValue(cacheKey, out var cached))
            return cached;

        List<(string, string, double)> results;

        if (useGeometric && _geometricSelector != null)
        {
            results = _geometricSelector.MultiHop(subject, relations, 3);
        }
        else
        {
            results = new();
            var current = subject;
            foreach (var relation in relations)
            {
                var inferred = Infer(current, relation);
                if (inferred.Count == 0) break;
                var best = inferred.First();
                results.Add((best, relation, 1.0));
                current = best;
            }
        }

        _multiHopCache[cacheKey] = results;

        if (_multiHopCache.Count > 1000)
        {
            var oldest = _multiHopCache.Keys.First();
            _multiHopCache.Remove(oldest);
        }

        return results;
    }

    public string ExplainMultiHop(string subject, string[] relations)
    {
        var chain = MultiHopChain(subject, relations);
        if (chain.Count == 0)
            return $"No path found from '{subject}' via {string.Join(" → ", relations)}";

        var parts = new List<string> { subject };
        foreach (var (target, rel, score) in chain)
            parts.Add($"[{rel}:{score:F2}]→ {target}");

        return string.Join(" ", parts);
    }
}
