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
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly ILogger<KnowledgeGraph> _logger;

    public KnowledgeGraph(ILogger<KnowledgeGraph> logger)
    {
        _logger = logger;
    }

    public void AddEntity(Entity entity)
    {
        _lock.EnterWriteLock();
        try
        {
            _nodesIndex[entity.Id] = entity;
            _adjacency.TryAdd(entity.Id, new());
        }
        finally { _lock.ExitWriteLock(); }
    }

    public async Task SaveToDiskAsync(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, ".livingtree", "knowledge_graph.json");
        var dir = Path.GetDirectoryName(path);
        if (dir != null) Directory.CreateDirectory(dir);
        var data = new Dictionary<string, object>
        {
            ["entities"] = _nodesIndex,
            ["triplets"] = _triplets.Select(t => new { t.Subject, t.Predicate, t.Object, t.Confidence }).ToList(),
            ["saved_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(data));
    }

    public void LoadFromDisk(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, ".livingtree", "knowledge_graph.json");
        if (!File.Exists(path)) return;
        try
        {
            var json = File.ReadAllText(path);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("entities", out var entities))
                foreach (var e in entities.EnumerateArray()) { var ent = new Entity(e.GetProperty("id").GetString() ?? "", e.GetProperty("label").GetString() ?? ""); AddEntity(ent); }
            if (root.TryGetProperty("triplets", out var triplets))
                foreach (var t in triplets.EnumerateArray())
                {
                    var triplet = new Triplet(t.GetProperty("subject").GetString() ?? "", t.GetProperty("predicate").GetString() ?? "", t.GetProperty("object").GetString() ?? "", Confidence: t.TryGetProperty("confidence", out var c) && c.TryGetDouble(out var conf) ? conf : 0.5);
                    AddTripletsToGraph(new() { triplet });
                }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load knowledge graph from disk: {Path}", path);
        }
    }

    public void AddRelation(string sourceId, string targetId, string relation,
        Dictionary<string, object>? properties = null)
    {
        _lock.EnterWriteLock();
        try
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
        finally { _lock.ExitWriteLock(); }
    }

    public List<Dictionary<string, object>> QueryGraph(Dictionary<string, object> filter)
    {
        _lock.EnterReadLock();
        try
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
        finally { _lock.ExitReadLock(); }
    }

    public List<string> FindPath(string startId, string endId)
    {
        _lock.EnterReadLock();
        try
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
        finally { _lock.ExitReadLock(); }
    }

    public KnowledgeGraph GetSubgraph(List<string> ids)
    {
        _lock.EnterReadLock();
        try
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
        finally { _lock.ExitReadLock(); }
    }

    public List<string> EntityLinking(string text)
    {
        _lock.EnterReadLock();
        try
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
        finally { _lock.ExitReadLock(); }
    }

    public List<Triplet> ExtractTriplets(string text)
    {
        return ExtractTripletsRegex(text);
    }

    public int AddTripletsToGraph(List<Triplet> triplets)
    {
        _lock.EnterWriteLock();
        try
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
        finally { _lock.ExitWriteLock(); }
    }

    public List<Triplet> GetTriplets()
    {
        _lock.EnterReadLock();
        try
        {
            return _triplets.ToList();
        }
        finally { _lock.ExitReadLock(); }
    }

    public Dictionary<string, object> GetStats()
    {
        _lock.EnterReadLock();
        try
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
        finally { _lock.ExitReadLock(); }
    }

    public static string EntityId(string label)
    {
        var normalized = Regex.Replace(label.Trim().ToLowerInvariant(), @"\s+", "_");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..12].ToLowerInvariant();
    }

    public static List<Triplet> ExtractTripletsRegex(string text)
    {
        var results = new List<Triplet>();
        var patterns = new (string Pattern, string Pred)[]
        {
            (@"(?<subject>.+?)\s+(?:是|is|are)\s+(?<object>.+?)[。.]", "is_a"),
            (@"(?<subject>.+?)\s+(?:属于|belongs to)\s+(?<object>.+?)[。.]", "belongs_to"),
            (@"(?<subject>.+?)\s+(?:开发|developed|created)\s+(?<object>.+?)[。.]", "developed"),
            (@"(?<subject>.+?)\s+(?:用于|used in|used for)\s+(?<object>.+?)[。.]", "used_in"),
            (@"(?<subject>.+?)\s+(?:导致|causes|results in)\s+(?<object>.+?)[。.]", "causes"),
            (@"(?<subject>.+?)\s+(?:包含|contains|includes|has)\s+(?<object>.+?)[。.]", "contains"),
            (@"(?<subject>.+?)\s+(?:位于|located in|at)\s+(?<object>.+?)[。.]", "located_in"),
            (@"(?<subject>.+?)\s+(?:需要|requires|needs)\s+(?<object>.+?)[。.]", "requires"),
            (@"(?<subject>.+?)\s+(?:产生|produces|generates)\s+(?<object>.+?)[。.]", "produces"),
            (@"(?<subject>.+?)\s+(?:等于|equals)\s+(?<object>.+?)[。.]", "equals"),
            (@"(?<subject>.+?)\s+(?:由.*组成|consists of|composed of)\s+(?<object>.+?)[。.]", "composed_of"),
            (@"(?<subject>.+?)\s+(?:与.*相关|related to|associated with)\s+(?<object>.+?)[。.]", "related_to"),
            (@"(?<subject>.+?)\s+(?:不同于|differs from)\s+(?<object>.+?)[。.]", "differs_from"),
            (@"(?<subject>.+?)\s+(?:优于|better than|superior to)\s+(?<object>.+?)[。.]", "better_than"),
            (@"(?<subject>.+?)\s+(?:应用于|applied to|applied in)\s+(?<object>.+?)[。.]", "applied_to"),
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

        var definitionPatterns = new[]
        {
            @"(?<subject>.+?)[:：]\s*(?<object>.+?)[。.]",
            @"(?<subject>.+?)\s*[-–—]\s*(?<object>.+?)[。.]",
        };

        foreach (var pattern in definitionPatterns)
        {
            foreach (Match m in Regex.Matches(text, pattern, RegexOptions.IgnoreCase))
            {
                var subject = m.Groups["subject"].Value.Trim();
                var obj = m.Groups["object"].Value.Trim();
                if (subject.Length > 1 && obj.Length > 2 && subject.Length < 50 && obj.Length < 200)
                {
                    results.Add(new Triplet(subject, "defined_as", obj, text, 0.6));
                }
            }
        }

        return results;
    }
}
