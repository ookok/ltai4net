using LiteDB;

namespace LTAI.Agent.Vector;

/// <summary>
/// Unified Graph + Vector Store on LiteDB.
/// Replaces both LiteDbVectorStore (old) and GraphStore (preliminary).
/// Single LiteDB file with: gnodes (typed entities + embeddings), gedges (relationships).
/// Supports incremental updates and maintenance.
/// </summary>
public sealed class GraphStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly string _dbPath;
    private readonly SemaphoreSlim _compactLock = new(1, 1);


    public GraphStore(string dbPath)
    {
        _dbPath = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _db = new LiteDatabase(dbPath);
        InitIndexes();
    }

    private void InitIndexes()
    {
        var nodes = _db.GetCollection("gnodes");
        nodes.EnsureIndex("type");
        nodes.EnsureIndex("name");
        nodes.EnsureIndex("src");       // source identifier for incremental update
        nodes.EnsureIndex("ts");        // timestamp for GC

        var edges = _db.GetCollection("gedges");
        edges.EnsureIndex("source");
        edges.EnsureIndex("target");
        edges.EnsureIndex("relation");
    }

    // ═══════════════════════════════════════════
    //  Nodes — CRUD
    // ═══════════════════════════════════════════

    public void UpsertNode(string id, string type, string name,
        float[]? embedding = null, Dictionary<string, object?>? metadata = null,
        string? source = null)
    {
        var col = _db.GetCollection("gnodes");
        var doc = new BsonDocument
        {
            ["_id"] = id,
            ["type"] = type,
            ["name"] = name,
            ["ts"] = DateTime.UtcNow.ToString("O"),
        };
        if (source != null) doc["src"] = source;
        if (embedding != null)
            doc["v"] = new BsonArray(embedding.Select(f => new BsonValue((double)f)));
        if (metadata != null)
            foreach (var (k, v) in metadata)
                doc[k] = ToBson(v);
        col.Upsert(doc);
    }

    public BsonDocument? GetNode(string id) =>
        _db.GetCollection("gnodes").FindById(id);

    public bool NodeExists(string id) =>
        _db.GetCollection("gnodes").FindById(id) != null;

    public List<BsonDocument> GetNodesByType(string type) =>
        _db.GetCollection("gnodes").Find(Query.EQ("type", type)).ToList();

    public List<BsonDocument> GetNodesBySource(string source) =>
        _db.GetCollection("gnodes").Find(Query.EQ("src", source)).ToList();

    public long NodeCount() => _db.GetCollection("gnodes").Count();

    /// <summary>
    /// Delete a node and cascade-delete all its edges.
    /// </summary>
    public void DeleteNode(string id)
    {
        _db.GetCollection("gnodes").Delete(id);
        var ec = _db.GetCollection("gedges");
        foreach (var e in ec.Find(Query.Or(Query.EQ("source", id), Query.EQ("target", id))))
            ec.Delete(e["_id"].AsObjectId);
    }

    /// <summary>
    /// Delete all nodes from a source (for incremental reindex) + cascade edges.
    /// </summary>
    public void DeleteSource(string source)
    {
        var nodes = GetNodesBySource(source);
        foreach (var n in nodes)
            DeleteNode(n["_id"].AsString);
    }

    // ═══════════════════════════════════════════
    //  Vector Search (replaces LiteDbVectorStore)
    // ═══════════════════════════════════════════

    /// <summary>Vector search across all nodes with embeddings.</summary>
    public List<(string id, float score)> Search(float[] query, int topN = 10,
        string? typeFilter = null, float minScore = 0.3f)
    {
        var col = _db.GetCollection("gnodes");
        var all = typeFilter != null
            ? col.Find(Query.EQ("type", typeFilter)).ToList()
            : col.FindAll().ToList();

        return all
            .Where(d => d.ContainsKey("v"))
            .Select(d =>
            {
                var vec = d["v"].AsArray.Select(x => (float)x.AsDouble).ToArray();
                return (id: d["_id"].AsString, score: CosineSim(query, vec));
            })
            .Where(r => r.score >= minScore)
            .OrderByDescending(r => r.score)
            .Take(topN)
            .ToList();
    }

    /// <summary>Search returning full BsonDocuments (for context enrichment).</summary>
    public List<BsonDocument> SearchNodes(float[] query, int topN = 10,
        string? typeFilter = null, float minScore = 0.3f)
    {
        var results = Search(query, topN, typeFilter, minScore);
        var col = _db.GetCollection("gnodes");
        return results
            .Select(r => col.FindById(r.id))
            .Where(d => d != null)
            .Cast<BsonDocument>()
            .ToList();
    }

    // ═══════════════════════════════════════════
    //  Edges
    // ═══════════════════════════════════════════

    public void AddEdge(string sourceId, string targetId, string relation, double weight = 1.0)
    {
        var col = _db.GetCollection("gedges");
        if (col.Exists(Query.And(
            Query.EQ("source", sourceId),
            Query.EQ("target", targetId),
            Query.EQ("relation", relation))))
            return;
        col.Insert(new BsonDocument
        {
            ["source"] = sourceId,
            ["target"] = targetId,
            ["relation"] = relation,
            ["weight"] = weight,
        });
    }

    public List<(string src, string tgt, string rel, double w)> GetEdges(
        string? nodeId = null, string? relation = null)
    {
        var col = _db.GetCollection("gedges").FindAll().AsEnumerable();
        if (nodeId != null)
            col = col.Where(e => e["source"].AsString == nodeId || e["target"].AsString == nodeId);
        if (relation != null)
            col = col.Where(e => e["relation"].AsString == relation);
        return col.Select(e => (e["source"].AsString, e["target"].AsString,
            e["relation"].AsString, e["weight"].AsDouble)).ToList();
    }

    public long EdgeCount() => _db.GetCollection("gedges").Count();

    // ═══════════════════════════════════════════
    //  Graph Traversal (BFS)
    // ═══════════════════════════════════════════

    public List<string> TraverseBfs(string startId, string? relation = null,
        int maxDepth = 3, int maxNodes = 50)
    {
        var visited = new HashSet<string> { startId };
        var queue = new Queue<(string id, int depth)>();
        queue.Enqueue((startId, 0));

        while (queue.Count > 0 && visited.Count < maxNodes)
        {
            var (current, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;

            foreach (var (src, tgt, _, _) in GetEdges(current, relation))
            {
                var next = src == current ? tgt : src;
                if (visited.Add(next) && visited.Count < maxNodes)
                    queue.Enqueue((next, depth + 1));
            }
        }
        return visited.ToList();
    }

    // ═══════════════════════════════════════════
    //  Maintenance
    // ═══════════════════════════════════════════

    /// <summary>Prune nodes older than the given cutoff. Returns count removed.</summary>
    public int PruneBefore(DateTime cutoff)
    {
        var col = _db.GetCollection("gnodes");
        var old = col.FindAll()
            .Where(d =>
            {
                if (!d.ContainsKey("ts")) return false;
                return DateTime.TryParse(d["ts"].AsString, out var t) && t < cutoff;
            })
            .ToList();

        foreach (var d in old)
            DeleteNode(d["_id"].AsString);
        return old.Count;
    }

    /// <summary>Prune sources that no longer exist on disk.</summary>
    public int PruneStaleSources(string rootDir, string searchPattern = "*.cs")
    {
        var validSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(rootDir))
        {
            foreach (var f in Directory.GetFiles(rootDir, searchPattern, SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") && !f.Contains("\\dist\\")))
            {
                validSources.Add(Path.GetRelativePath(rootDir, f).Replace('\\', '/'));
            }
        }

        var col = _db.GetCollection("gnodes");
        var stale = col.FindAll()
            .Where(d => d.ContainsKey("src") && !validSources.Contains(d["src"].AsString))
            .ToList();

        foreach (var d in stale)
            DeleteNode(d["_id"].AsString);
        return stale.Count;
    }

    /// <summary>Full maintenance: prune stale + compact. Returns (pruned, compacted).</summary>
    public (int pruned, long beforeBytes, long afterBytes) RunMaintenance(string rootDir)
    {
        var dbPath = _dbPath;
        var before = new FileInfo(dbPath).Length;
        var pruned = PruneStaleSources(rootDir);
        Compact();
        var after = new FileInfo(dbPath).Length;
        return (pruned, before, after);
    }

    /// <summary>Thread-safe compact with exclusive lock (prevents concurrent Rebuild corruption).</summary>
    public void Compact()
    {
        _compactLock.Wait();
        try { _db.Rebuild(); }
        finally { _compactLock.Release(); }
    }

    public string Stats()
    {
        var nodes = _db.GetCollection("gnodes").Count();
        var edges = _db.GetCollection("gedges").Count();
        var types = _db.GetCollection("gnodes").FindAll()
            .GroupBy(d => d["type"].AsString)
            .Select(g => $"{g.Key}: {g.Count()}")
            .ToList();
        return $"Nodes: {nodes}, Edges: {edges}\nTypes: {string.Join(", ", types)}";
    }

    // ═══════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════

    private static BsonValue ToBson(object? v) => v switch
    {
        string s => s,
        int i => i,
        long l => l,
        double d => d,
        float f => (double)f,
        bool b => b,
        null => BsonValue.Null,
        _ => v.ToString()!
    };

    private static float CosineSim(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        float dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na > 0 && nb > 0 ? dot / (MathF.Sqrt(na) * MathF.Sqrt(nb)) : 0;
    }

    public void Dispose() => _db.Dispose();
}
