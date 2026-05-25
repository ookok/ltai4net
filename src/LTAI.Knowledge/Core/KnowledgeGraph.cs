using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LTAI.Core.System;
using LTAI.Knowledge.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LTAI.Knowledge.Core;

public class KnowledgeGraph : IDisposable
{
    private readonly Dictionary<string, Entity> _nodesIndex = new();
    private readonly Dictionary<string, Dictionary<string, List<string>>> _adjacency = new();
    private readonly List<Triplet> _triplets = new();
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly ILogger<KnowledgeGraph> _logger;
    private readonly SqliteConnection _db;
    private bool _disposed;

    public KnowledgeGraph(ILogger<KnowledgeGraph> logger, DataPathResolver? dataPath = null, string? dbPath = null)
    {
        _logger = logger;
        var effectivePath = dbPath
            ?? dataPath?.GetPath("knowledge_graph.db")
            ?? Path.Combine(AppContext.BaseDirectory, ".livingtree", "knowledge_graph.db");
        var dir = Path.GetDirectoryName(effectivePath);
        if (dir != null) Directory.CreateDirectory(dir);
        _db = new SqliteConnection($"Data Source={effectivePath}");
        _db.Open();
        InitializeSchema();

        var jsonPath = Path.Combine(dir ?? ".livingtree", "knowledge_graph.json");
        if (File.Exists(jsonPath))
        {
            LoadFromDisk(jsonPath);
            try { File.Delete(jsonPath); } catch (Exception ex) { _logger.LogWarning(ex, "KnowledgeGraph: Failed to delete legacy JSON file"); }
        }
    }

    private void InitializeSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS entities (
                id TEXT PRIMARY KEY, label TEXT, properties TEXT);
            CREATE TABLE IF NOT EXISTS triplets (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                subject TEXT, predicate TEXT, object TEXT, confidence REAL, source_text TEXT);
            CREATE VIRTUAL TABLE IF NOT EXISTS entities_fts USING fts5(
                id, label, content=entities, content_rowid=rowid);
            CREATE TABLE IF NOT EXISTS relations (
                source_id TEXT, target_id TEXT, relation TEXT,
                PRIMARY KEY (source_id, target_id, relation));
            CREATE INDEX IF NOT EXISTS idx_triplets_subject ON triplets(subject);
            CREATE INDEX IF NOT EXISTS idx_triplets_object ON triplets(object);
            CREATE INDEX IF NOT EXISTS idx_triplets_pred ON triplets(predicate);
            """;
        cmd.ExecuteNonQuery();
    }

    public void AddEntity(Entity entity)
    {
        _lock.EnterWriteLock();
        try { PersistEntity(entity); }
        finally { _lock.ExitWriteLock(); }
    }

    private void PersistEntity(Entity entity)
    {
        _nodesIndex[entity.Id] = entity;
        _adjacency.TryAdd(entity.Id, new());

        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO entities VALUES (@id, @label, @props)";
        cmd.Parameters.AddWithValue("@id", entity.Id);
        cmd.Parameters.AddWithValue("@label", entity.Label);
        cmd.Parameters.AddWithValue("@props",
            entity.Properties != null
                ? System.Text.Json.JsonSerializer.Serialize(entity.Properties)
                : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void AddRelation(string sourceId, string targetId, string relation,
        Dictionary<string, object>? properties = null)
    {
        _lock.EnterWriteLock();
        try { PersistRelation(sourceId, targetId, relation, properties); }
        finally { _lock.ExitWriteLock(); }
    }

    private void PersistRelation(string sourceId, string targetId, string relation,
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

        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO relations VALUES (@sid, @tid, @rel)";
        cmd.Parameters.AddWithValue("@sid", sourceId);
        cmd.Parameters.AddWithValue("@tid", targetId);
        cmd.Parameters.AddWithValue("@rel", relation);
        cmd.ExecuteNonQuery();
    }

    public async Task SaveToDiskAsync(string? path = null)
    {
        _ = path;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public void LoadFromDisk(string? path = null)
    {
        if (path != null && File.Exists(path))
        {
            LoadFromJsonFile(path);
            return;
        }

        LoadFromSqlite();
    }

    private void LoadFromJsonFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            _lock.EnterWriteLock();
            try
            {
                if (root.TryGetProperty("entities", out var entities))
                {
                    foreach (var e in entities.EnumerateArray())
                    {
                        var ent = new Entity(
                            e.GetProperty("id").GetString() ?? "",
                            e.GetProperty("label").GetString() ?? "");
                        PersistEntity(ent);
                    }
                }
                if (root.TryGetProperty("triplets", out var triplets))
                {
                    foreach (var t in triplets.EnumerateArray())
                    {
                        var subj = t.GetProperty("subject").GetString() ?? "";
                        var pred = t.GetProperty("predicate").GetString() ?? "";
                        var obj = t.GetProperty("object").GetString() ?? "";
                        var conf = t.TryGetProperty("confidence", out var c) && c.TryGetDouble(out var d) ? d : 0.5;
                        var triplet = new Triplet(subj, pred, obj, Confidence: conf);
                        PersistTriplet(triplet);
                    }
                }
            }
            finally { _lock.ExitWriteLock(); }

            _logger.LogInformation("KnowledgeGraph: Imported from JSON, migrated to SQLite");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to import knowledge graph from JSON: {Path}", path);
        }
    }

    private void LoadFromSqlite()
    {
        _lock.EnterWriteLock();
        try
        {
            _nodesIndex.Clear();
            _adjacency.Clear();
            _triplets.Clear();

            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT id, label, properties FROM entities";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var ent = new Entity(reader.GetString(0), reader.GetString(1));
                    if (!reader.IsDBNull(2))
                    {
                        try
                        {
                            var props = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(reader.GetString(2));
                            var propInfo = typeof(Entity).GetProperty("Properties");
                            if (propInfo != null && props != null)
                                propInfo.SetValue(ent, props);
                        }
                        catch (Exception ex) { _logger.LogWarning(ex, "KnowledgeGraph: Failed to deserialize entity properties"); }
                    }
                }
            }

            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT source_id, target_id, relation FROM relations";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var sid = reader.GetString(0);
                    var tid = reader.GetString(1);
                    var rel = reader.GetString(2);
                    _adjacency.TryAdd(sid, new());
                    _adjacency[sid].TryAdd(rel, new());
                    if (!_adjacency[sid][rel].Contains(tid))
                        _adjacency[sid][rel].Add(tid);
                }
            }

            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT subject, predicate, object, confidence, source_text FROM triplets";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    _triplets.Add(new Triplet(
                        reader.GetString(0), reader.GetString(1), reader.GetString(2),
                        reader.IsDBNull(4) ? null! : reader.GetString(4),
                        reader.GetDouble(3)));
                }
            }
        }
        finally { _lock.ExitWriteLock(); }
    }

    public List<Triplet> SearchTriplets(string query, int limit = 20)
    {
        _lock.EnterReadLock();
        try
        {
            var results = new List<Triplet>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT subject, predicate, object, confidence, source_text FROM triplets WHERE subject LIKE @q OR predicate LIKE @q OR object LIKE @q LIMIT @limit";
            cmd.Parameters.AddWithValue("@q", $"%{query}%");
            cmd.Parameters.AddWithValue("@limit", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new Triplet(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(4) ? null! : reader.GetString(4),
                    reader.GetDouble(3)));
            }
            return results;
        }
        finally { _lock.ExitReadLock(); }
    }

    public List<Entity> SearchEntities(string query, int limit = 20)
    {
        _lock.EnterReadLock();
        try
        {
            var results = new List<Entity>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT e.id, e.label, e.properties FROM entities e JOIN entities_fts f ON e.rowid = f.rowid WHERE entities_fts MATCH @q ORDER BY rank LIMIT @limit";
            cmd.Parameters.AddWithValue("@q", $"\"{query}\"");
            cmd.Parameters.AddWithValue("@limit", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var ent = new Entity(reader.GetString(0), reader.GetString(1));
                if (!reader.IsDBNull(2))
                {
                    try
                    {
                        var props = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(reader.GetString(2));
                        var propInfo = typeof(Entity).GetProperty("Properties");
                        if (propInfo != null && props != null)
                            propInfo.SetValue(ent, props);
                    }
                    catch (Exception ex) { _logger.LogWarning(ex, "KnowledgeGraph: Failed to deserialize entity properties in search"); }
                }
                results.Add(ent);
            }
            return results;
        }
        finally { _lock.ExitReadLock(); }
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
                var props = entity.Properties ?? new();
                foreach (var (k, v) in filter)
                {
                    if (!props.TryGetValue(k, out var val) || !Equals(val, v))
                    { match = false; break; }
                }
                if (match)
                    results.Add(new() { ["id"] = id, ["attributes"] = props });
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
            var sub = new LTAI.Knowledge.Core.KnowledgeGraph(_logger);
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
                PersistTriplet(t);
                count++;
            }
            return count;
        }
        finally { _lock.ExitWriteLock(); }
    }

    private void PersistTriplet(Triplet t)
    {
        var sId = EntityId(t.Subject);
        var oId = EntityId(t.Object);
        PersistEntity(new Entity(sId, t.Subject));
        PersistEntity(new Entity(oId, t.Object));
        PersistRelation(sId, oId, t.Predicate);
        _triplets.Add(t);

        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT INTO triplets (subject, predicate, object, confidence, source_text) VALUES (@s, @p, @o, @c, @src)";
        cmd.Parameters.AddWithValue("@s", t.Subject);
        cmd.Parameters.AddWithValue("@p", t.Predicate);
        cmd.Parameters.AddWithValue("@o", t.Object);
        cmd.Parameters.AddWithValue("@c", t.Confidence);
        cmd.Parameters.AddWithValue("@src", (object?)t.SourceText ?? DBNull.Value);
        cmd.ExecuteNonQuery();
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

            long dbSize = 0;
            try { dbSize = new FileInfo(_db.DataSource).Length; } catch (Exception ex) { _logger.LogWarning(ex, "KnowledgeGraph: Failed to get DB file size"); }

            return new()
            {
                ["entity_count"] = _nodesIndex.Count,
                ["edge_count"] = edges,
                ["by_relation_type"] = byType,
                ["triplet_count"] = _triplets.Count,
                ["storage"] = "SQLite",
                ["db_size_bytes"] = dbSize,
                ["predictability"] = ComputePredictabilitySnapshot()
            };
        }
        finally { _lock.ExitReadLock(); }
    }

    internal List<Entity> GetAllNodes()
    {
        _lock.EnterReadLock();
        try { return _nodesIndex.Values.ToList(); }
        finally { _lock.ExitReadLock(); }
    }

    internal Dictionary<string, Dictionary<string, List<string>>> GetAdjacencyForAnalysis()
    {
        _lock.EnterReadLock();
        try
        {
            var snapshot = new Dictionary<string, Dictionary<string, List<string>>>();
            foreach (var (key, edges) in _adjacency)
            {
                var copy = new Dictionary<string, List<string>>();
                foreach (var (rel, targets) in edges)
                    copy[rel] = new List<string>(targets);
                snapshot[key] = copy;
            }
            return snapshot;
        }
        finally { _lock.ExitReadLock(); }
    }

    private object ComputePredictabilitySnapshot()
    {
        try
        {
            var result = KnowledgeGraphAnalytics.Analyze(this, sampleSize: 500);
            return new
            {
                pi = Math.Round(result.PredictabilityIndex, 3),
                reliability = result.IsReliable ? "high" : "low",
                graph_type = result.ClassifiedType.ToString(),
                heterogeneity = Math.Round(result.DegreeHeterogeneity, 3),
                clustering = Math.Round(result.ClusteringCoefficient, 3),
                avg_degree = Math.Round(result.AverageDegree, 1),
                recommendation = result.IsReliable
                    ? "Graph structure is dense enough for reliable KB reasoning"
                    : "Sparse graph — prefer vector search over graph traversal"
            };
        }
        catch { return new { pi = 0, reliability = "unknown" }; }
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lock.Dispose();
        _db.Close();
        _db.Dispose();
    }
}
