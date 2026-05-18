using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LTAI.Vector.Knowledge.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Vector.Knowledge;

public class KnowledgeGraph
{
    private readonly Dictionary<string, Entity> _nodesIndex = new();
    private readonly Dictionary<string, Dictionary<string, List<string>>> _adjacency = new();
    private readonly List<Triplet> _triplets = new();
    private readonly ILogger<KnowledgeGraph> _logger;

    public KnowledgeGraph(ILogger<KnowledgeGraph> logger)
    {
        _logger = logger;
    }

    public void AddEntity(Entity entity)
    {
        _nodesIndex[entity.Id] = entity;
        _adjacency.TryAdd(entity.Id, new());
    }

    public void AddRelation(string sourceId, string targetId, string relation,
        Dictionary<string, object>? properties = null)
    {
        if (!_nodesIndex.ContainsKey(sourceId))
            _nodesIndex[sourceId] = new Entity(sourceId, sourceId);
        if (!_nodesIndex.ContainsKey(targetId))
            _nodesIndex[targetId] = new Entity(targetId, targetId);

        _adjacency.TryAdd(sourceId, new());
        _adjacency[sourceId].TryAdd(relation, new());
        if (!_adjacency[sourceId][relation].Contains(targetId))
            _adjacency[sourceId][relation].Add(targetId);
    }

    public List<Dictionary<string, object>> QueryGraph(Dictionary<string, object> filter)
    {
        var results = new List<Dictionary<string, object>>();
        foreach (var (id, entity) in _nodesIndex)
        {
            bool match = true;
            foreach (var (k, v) in filter)
            {
                if (!entity.Properties.TryGetValue(k, out var val) || !Equals(val, v))
                { match = false; break; }
            }
            if (match)
                results.Add(new() { ["id"] = id, ["attributes"] = entity.Properties });
        }
        return results;
    }

    public List<string> FindPath(string startId, string endId)
    {
        if (!_nodesIndex.ContainsKey(startId) || !_nodesIndex.ContainsKey(endId))
            return new();

        var visited = new HashSet<string> { startId };
        var queue = new Queue<List<string>>();
        queue.Enqueue(new() { startId });

        while (queue.Count > 0)
        {
            var path = queue.Dequeue();
            var current = path[^1];

            if (_adjacency.TryGetValue(current, out var relations))
            {
                foreach (var targets in relations.Values)
                {
                    foreach (var target in targets)
                    {
                        if (target == endId)
                        { path.Add(target); return path; }
                        if (!visited.Contains(target))
                        {
                            visited.Add(target);
                            var newPath = new List<string>(path) { target };
                            queue.Enqueue(newPath);
                        }
                    }
                }
            }
        }
        return new();
    }

    public KnowledgeGraph GetSubgraph(List<string> ids)
    {
        var sub = new KnowledgeGraph(_logger);
        foreach (var id in ids)
            if (_nodesIndex.TryGetValue(id, out var e))
                sub.AddEntity(e);

        foreach (var id in ids)
        {
            if (_adjacency.TryGetValue(id, out var relations))
                foreach (var (rel, targets) in relations)
                    foreach (var t in targets)
                        if (ids.Contains(t))
                            sub.AddRelation(id, t, rel);
        }
        return sub;
    }

    public List<string> EntityLinking(string text)
    {
        var found = new List<(string id, int offset, int length)>();
        foreach (var (id, entity) in _nodesIndex)
        {
            int idx = 0;
            while ((idx = text.IndexOf(entity.Label, idx, StringComparison.OrdinalIgnoreCase)) != -1)
            {
                found.Add((id, idx, entity.Label.Length));
                idx += entity.Label.Length;
            }
        }
        found.Sort((a, b) => a.length != b.length ? b.length.CompareTo(a.length) : a.offset.CompareTo(b.offset));

        var result = new List<string>();
        var occupied = new HashSet<int>();
        foreach (var (id, offset, length) in found)
        {
            bool overlap = false;
            for (int i = offset; i < offset + length; i++)
                if (occupied.Contains(i)) { overlap = true; break; }
            if (!overlap)
            {
                result.Add(id);
                for (int i = offset; i < offset + length; i++) occupied.Add(i);
            }
        }
        return result;
    }

    public List<Triplet> ExtractTriplets(string text)
    {
        return ExtractTripletsRegex(text);
    }

    public int AddTripletsToGraph(List<Triplet> triplets)
    {
        int count = 0;
        foreach (var t in triplets)
        {
            var sId = EntityId(t.Subject);
            var oId = EntityId(t.Object);
            AddEntity(new Entity(sId, t.Subject));
            AddEntity(new Entity(oId, t.Object));
            AddRelation(sId, oId, t.Predicate);
            _triplets.Add(t);
            count++;
        }
        return count;
    }

    public List<Triplet> GetTriplets() => _triplets;

    public Dictionary<string, object> GetStats()
    {
        var byType = new Dictionary<string, int>();
        int edges = 0;
        foreach (var (_, relations) in _adjacency)
            foreach (var (rel, targets) in relations)
            {
                edges += targets.Count;
                byType[rel] = byType.GetValueOrDefault(rel) + targets.Count;
            }

        return new()
        {
            ["entity_count"] = _nodesIndex.Count,
            ["edge_count"] = edges,
            ["by_relation_type"] = byType,
            ["triplet_count"] = _triplets.Count
        };
    }

    public static string EntityId(string label)
    {
        var normalized = Regex.Replace(label.Trim().ToLowerInvariant(), @"\s+", "_");
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(normalized)))[..12].ToLowerInvariant();
    }

    public static List<Triplet> ExtractTripletsRegex(string text)
    {
        var results = new List<Triplet>();
        var patterns = new (string Pattern, string Pred)[]
        {
            (@"(?<subject>.+?)\s+(?:是|is)\s+(?<object>.+?)[。.]", "is_a"),
            (@"(?<subject>.+?)\s+(?:属于|belongs to)\s+(?<object>.+?)[。.]", "belongs_to"),
            (@"(?<subject>.+?)\s+(?:开发|developed)\s+(?<object>.+?)[。.]", "developed"),
            (@"(?<subject>.+?)\s+(?:用于|used in)\s+(?<object>.+?)[。.]", "used_in"),
            (@"(?<subject>.+?)\s+(?:导致|causes)\s+(?<object>.+?)[。.]", "causes"),
        };

        foreach (var (pattern, pred) in patterns)
        {
            foreach (Match m in Regex.Matches(text, pattern, RegexOptions.IgnoreCase))
            {
                results.Add(new Triplet(
                    m.Groups["subject"].Value.Trim(),
                    pred,
                    m.Groups["object"].Value.Trim(),
                    text, 0.7));
            }
        }
        return results;
    }
}
