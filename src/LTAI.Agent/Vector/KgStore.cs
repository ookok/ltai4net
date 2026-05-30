// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  KgStore — SQLite knowledge graph with FTS5 + CTE traversal
//
//  Optimizations:
//  - Read/write connection pool (WAL mode, no lock contention)
//  - Prepared statement cache (frequent SQL pre-compiled)
//  - Result cache (LRU with TTL for SearchFts)
//  - Weighted BM25 rank (node kind boosts)
//  - Weighted BFS (edge weights + kind scores)
// ═══════════════════════════════════════════════════════════════

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;

namespace LTAI.Agent.Vector;

public sealed partial class KgStore : IDisposable
{
    // ═══════════════════════════════════════════
    //  Connections & pool
    // ═══════════════════════════════════════════

    private readonly SqliteConnection _writer;
    private readonly SqliteConnection _reader;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _dbPath;
    private bool _disposed;
    public string DbPath => _dbPath;

    // ═══════════════════════════════════════════
    //  Prepared statement cache
    // ═══════════════════════════════════════════

    private readonly ConcurrentDictionary<string, SqliteCommand> _writeCmdCache = new();
    private readonly ConcurrentDictionary<string, SqliteCommand> _readCmdCache = new();

    // ═══════════════════════════════════════════
    //  Result cache (LRU with TTL)
    // ═══════════════════════════════════════════

    private readonly MemoryCache _resultCache = new(new MemoryCacheOptions
    {
        SizeLimit = 256,
        ExpirationScanFrequency = TimeSpan.FromMinutes(2)
    });

    // ═══════════════════════════════════════════
    //  Construction
    // ═══════════════════════════════════════════

    public KgStore(string dbPath)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        // Writer connection
        _writer = new SqliteConnection($"Data Source={dbPath}");
        _writer.Open();
        InitConnection(_writer);

        // Reader connection (WAL = concurrent reads, no lock)
        _reader = new SqliteConnection($"Data Source={dbPath};Mode=ReadWrite");
        _reader.Open();
        InitConnection(_reader);

        InitSchema();
    }

    private static void InitConnection(SqliteConnection conn)
    {
        using var pragma = conn.CreateCommand();
        pragma.CommandText = @"
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA cache_size=-8000;            -- 8MB page cache
            PRAGMA auto_vacuum=INCREMENTAL;     -- 删除后自动回收页面（新DB生效）
            PRAGMA mmap_size=268435456;         -- 256MB 内存映射，加速大量读
            PRAGMA foreign_keys=ON;
        ";
        pragma.ExecuteNonQuery();
    }

    // ═══════════════════════════════════════════
    //  Prepared statement factory
    // ═══════════════════════════════════════════

    private SqliteCommand GetPreparedWrite(string sql)
    {
        return _writeCmdCache.GetOrAdd(sql, key =>
        {
            var cmd = _writer.CreateCommand();
            cmd.CommandText = key;
            cmd.Prepare();
            return cmd;
        });
    }

    private SqliteCommand GetPreparedRead(string sql)
    {
        return _readCmdCache.GetOrAdd(sql, key =>
        {
            var cmd = _reader.CreateCommand();
            cmd.CommandText = key;
            cmd.Prepare();
            return cmd;
        });
    }

    /// <summary>Execute a write command with exclusive lock.</summary>
    private T WriteLock<T>(Func<SqliteCommand, T> action, string sql,
        Action<SqliteCommand>? bindParams = null)
    {
        _writeLock.Wait();
        try
        {
            var cmd = GetPreparedWrite(sql);
            cmd.Parameters.Clear();
            bindParams?.Invoke(cmd);
            return action(cmd);
        }
        finally { _writeLock.Release(); }
    }

    private void WriteLockVoid(string sql, Action<SqliteCommand>? bindParams = null)
    {
        _writeLock.Wait();
        try
        {
            var cmd = GetPreparedWrite(sql);
            cmd.Parameters.Clear();
            bindParams?.Invoke(cmd);
            cmd.ExecuteNonQuery();
        }
        finally { _writeLock.Release(); }
    }

    // ═══════════════════════════════════════════
    //  Nodes — CRUD
    // ═══════════════════════════════════════════

    private const string SQL_UPSERT_NODE = """
        INSERT INTO Nodes(ext_id, kind, name, namespace, signature, source, props)
        VALUES (@ext_id, @kind, @name, @ns, @sig, @src, @props)
        ON CONFLICT(ext_id) DO UPDATE SET
            kind=@kind, name=@name, namespace=@ns, signature=@sig,
            source=@src, props=@props, updated_at=CURRENT_TIMESTAMP;
        SELECT id FROM Nodes WHERE ext_id = @ext_id;
        """;

    public long UpsertNode(string extId, string kind, string name,
        string? ns = null, string? signature = null,
        string? source = null, Dictionary<string, object?>? props = null)
    {
        var propsJson = props != null ? JsonSerializer.Serialize(props, KgStoreInternals.JsonOpts) : null;
        return WriteLock(cmd =>
        {
            return (long)cmd.ExecuteScalar()!;
        }, SQL_UPSERT_NODE, cmd =>
        {
            cmd.Parameters.AddWithValue("@ext_id", extId);
            cmd.Parameters.AddWithValue("@kind", kind);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@ns", (object?)ns ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@sig", (object?)signature ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@src", (object?)source ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@props", (object?)propsJson ?? DBNull.Value);
        });
    }

    private const string SQL_GET_NODE = "SELECT * FROM Nodes WHERE id = @id;";

    public NodeRow? GetNode(long id)
    {
        var cmd = GetPreparedRead(SQL_GET_NODE);
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@id", id);
        return ReadNodeRow(cmd.ExecuteReader());
    }

    private const string SQL_GET_NODE_BY_EXT = "SELECT * FROM Nodes WHERE ext_id = @ext_id;";

    public NodeRow? GetNodeByExtId(string extId)
    {
        var cmd = GetPreparedRead(SQL_GET_NODE_BY_EXT);
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@ext_id", extId);
        return ReadNodeRow(cmd.ExecuteReader());
    }

    private const string SQL_GET_NODES_BY_KIND = "SELECT * FROM Nodes WHERE kind = @kind ORDER BY name;";

    public List<NodeRow> GetNodesByKind(string kind)
    {
        var cmd = GetPreparedRead(SQL_GET_NODES_BY_KIND);
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@kind", kind);
        return ReadNodeRows(cmd.ExecuteReader());
    }

    private const string SQL_GET_NODES_BY_SOURCE = "SELECT * FROM Nodes WHERE source = @src ORDER BY name;";

    public List<NodeRow> GetNodesBySource(string source)
    {
        var cmd = GetPreparedRead(SQL_GET_NODES_BY_SOURCE);
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@src", source);
        return ReadNodeRows(cmd.ExecuteReader());
    }

    public List<NodeRow> SearchNodesByName(string namePattern, int limit = 20)
    {
        using var cmd = _reader.CreateCommand();
        cmd.CommandText = "SELECT * FROM Nodes WHERE name LIKE @pat ORDER BY kind, name LIMIT @lim;";
        cmd.Parameters.AddWithValue("@pat", $"%{EscapeLike(namePattern)}%");
        cmd.Parameters.AddWithValue("@lim", limit);
        return ReadNodeRows(cmd.ExecuteReader());
    }

    private const string SQL_DELETE_NODE = "DELETE FROM Nodes WHERE id = @id;";

    public bool DeleteNode(long id)
    {
        var deleted = WriteLock(cmd => cmd.ExecuteNonQuery() > 0, SQL_DELETE_NODE,
            cmd => cmd.Parameters.AddWithValue("@id", id));
        if (deleted) IncrementalVacuum(50);
        return deleted;
    }

    private const string SQL_DELETE_SOURCE = "DELETE FROM Nodes WHERE source = @src;";

    public int DeleteSource(string source)
    {
        var count = WriteLock(cmd => cmd.ExecuteNonQuery(), SQL_DELETE_SOURCE,
            cmd => cmd.Parameters.AddWithValue("@src", source));
        if (count > 0) IncrementalVacuum(200);
        return count;
    }

    private void IncrementalVacuum(int pages)
    {
        if (_disposed) return;
        WriteLockVoid($"PRAGMA incremental_vacuum({pages});");
    }

    private const string SQL_NODE_COUNT = "SELECT COUNT(*) FROM Nodes;";

    public long NodeCount()
    {
        var cmd = GetPreparedRead(SQL_NODE_COUNT);
        return (long)cmd.ExecuteScalar()!;
    }

    // ═══════════════════════════════════════════
    //  Edges
    // ═══════════════════════════════════════════

    private const string SQL_ADD_EDGE = """
        INSERT OR IGNORE INTO Edges(src, dst, rel, weight, props)
        VALUES (@src, @dst, @rel, @weight, @props);
        """;

    public void AddEdge(long srcId, long dstId, string relation, double weight = 1.0,
        Dictionary<string, object?>? props = null)
    {
        var propsJson = props != null ? JsonSerializer.Serialize(props, KgStoreInternals.JsonOpts) : null;
        WriteLockVoid(SQL_ADD_EDGE, cmd =>
        {
            cmd.Parameters.AddWithValue("@src", srcId);
            cmd.Parameters.AddWithValue("@dst", dstId);
            cmd.Parameters.AddWithValue("@rel", relation);
            cmd.Parameters.AddWithValue("@weight", weight);
            cmd.Parameters.AddWithValue("@props", (object?)propsJson ?? DBNull.Value);
        });
    }

    public List<EdgeRow> GetEdges(long? nodeId = null, string? relation = null)
    {
        var sql = new StringBuilder("SELECT * FROM Edges WHERE 1=1");
        if (nodeId.HasValue) sql.Append(" AND (src = @nid OR dst = @nid)");
        if (relation != null) sql.Append(" AND rel = @rel");
        sql.Append(" ORDER BY weight DESC;");

        using var cmd = _reader.CreateCommand();
        cmd.CommandText = sql.ToString();
        if (nodeId.HasValue) cmd.Parameters.AddWithValue("@nid", nodeId.Value);
        if (relation != null) cmd.Parameters.AddWithValue("@rel", relation);
        return ReadEdgeRows(cmd.ExecuteReader());
    }

    public int DeleteEdges(long nodeId, string? relation = null)
    {
        var sql = new StringBuilder("DELETE FROM Edges WHERE src = @nid OR dst = @nid");
        if (relation != null) sql.Append(" AND rel = @rel");
        return WriteLock(cmd => cmd.ExecuteNonQuery(), sql.ToString(), cmd =>
        {
            cmd.Parameters.AddWithValue("@nid", nodeId);
            if (relation != null) cmd.Parameters.AddWithValue("@rel", relation);
        });
    }

    // ═══════════════════════════════════════════
    //  Docs
    // ═══════════════════════════════════════════

    private const string SQL_ADD_DOC = """
        INSERT INTO Docs(node_id, text, lang, source)
        VALUES (@nid, @text, @lang, @src);
        SELECT last_insert_rowid();
        """;

    public long AddDoc(long nodeId, string text, string? lang = "code", string? source = null)
        => WriteLock(cmd => (long)cmd.ExecuteScalar()!, SQL_ADD_DOC, cmd =>
        {
            cmd.Parameters.AddWithValue("@nid", nodeId);
            cmd.Parameters.AddWithValue("@text", text);
            cmd.Parameters.AddWithValue("@lang", (object?)lang ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@src", (object?)source ?? DBNull.Value);
        });

    public void ReplaceDocs(long nodeId, List<(string text, string? lang, string? source)> docs)
    {
        _writeLock.Wait();
        try
        {
            using var tx = _writer.BeginTransaction();
            using var del = GetPreparedWrite("DELETE FROM Docs WHERE node_id = @nid;");
            del.Parameters.Clear();
            del.Parameters.AddWithValue("@nid", nodeId);
            del.ExecuteNonQuery();

            foreach (var (text, lang, src) in docs)
            {
                using var ins = _writer.CreateCommand();
                ins.CommandText = "INSERT INTO Docs(node_id, text, lang, source) VALUES (@nid, @text, @lang, @src);";
                ins.Parameters.AddWithValue("@nid", nodeId);
                ins.Parameters.AddWithValue("@text", text);
                ins.Parameters.AddWithValue("@lang", (object?)lang ?? DBNull.Value);
                ins.Parameters.AddWithValue("@src", (object?)src ?? DBNull.Value);
                ins.ExecuteNonQuery();
            }
            tx.Commit();
        }
        finally { _writeLock.Release(); }
    }

    private const string SQL_GET_DOCS = "SELECT * FROM Docs WHERE node_id = @nid ORDER BY id;";

    public List<DocRow> GetDocs(long nodeId)
    {
        var cmd = GetPreparedRead(SQL_GET_DOCS);
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@nid", nodeId);
        return ReadDocRows(cmd.ExecuteReader());
    }

    // ═══════════════════════════════════════════
    //  FTS5 BM25 Search (weighted)
    // ═══════════════════════════════════════════

    // BM25 weight multiplier per node kind (higher = more important in results)
    private static readonly Dictionary<string, double> KindBoost = new(StringComparer.OrdinalIgnoreCase)
    {
        ["method"] = 1.6,
        ["function"] = 1.6,
        ["class"] = 1.4,
        ["interface"] = 1.3,
        ["struct"] = 1.3,
        ["enum"] = 1.2,
        ["record"] = 1.2,
        ["property"] = 1.1,
        ["file"] = 1.0,
        ["document"] = 0.9,
        ["concept"] = 0.8,
        ["fact"] = 0.7,
    };

    private const string SQL_SEARCH_FTS = """
        SELECT f.node_id, f.text, f.kind,
               bm25(FTS_Index, 0.0, 1.0) * CASE n.kind
                   WHEN 'method' THEN 1.6 WHEN 'function' THEN 1.6
                   WHEN 'class' THEN 1.4 WHEN 'interface' THEN 1.3
                   WHEN 'struct' THEN 1.3 WHEN 'enum' THEN 1.2
                   WHEN 'record' THEN 1.2 WHEN 'file' THEN 1.0
                   ELSE 0.8
               END AS weighted_rank
        FROM FTS_Index f
        JOIN Nodes n ON n.id = f.node_id
        WHERE f.text MATCH @query
          AND (@kind IS NULL OR f.kind = @kind)
        ORDER BY weighted_rank DESC
        LIMIT @limit;
        """;

    public List<(long nodeId, string text, double rank, string kind)> SearchFts(
        string query, int topN = 30, string? kindFilter = null)
    {
        // Check result cache first
        var cacheKey = $"fts:{query}:{topN}:{kindFilter}";
        if (_resultCache.TryGetValue(cacheKey, out List<(long, string, double, string)>? cached))
            return cached!;

        var cmd = GetPreparedRead(SQL_SEARCH_FTS);
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@query", query);
        cmd.Parameters.AddWithValue("@kind", (object?)kindFilter ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@limit", topN);

        var results = ReadFtsResults(cmd.ExecuteReader());

        // Cache for 30 seconds (LRU eviction)
        _resultCache.Set(cacheKey, results, new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30)
        });

        return results;
    }

    // ═══════════════════════════════════════════
    //  CTE Graph Traversal (weighted BFS)
    // ═══════════════════════════════════════════

    public const int MaxTraversalDepth = 5;
    public const int MaxTraversalNodes = 200;

    public List<NodeRow> TraverseBfs(
        IEnumerable<long> startIds,
        string? relation = null,
        int maxDepth = 3,
        int maxNodes = 50)
    {
        maxDepth = Math.Min(maxDepth, MaxTraversalDepth);
        maxNodes = Math.Min(maxNodes, MaxTraversalNodes);

        var idList = startIds.Take(10).ToList();
        if (idList.Count == 0) return [];

        var inClause = string.Join(",", idList.Select((_, i) => $"@s{i}"));
        var relFilter = relation != null
            ? $"AND e.rel = '{relation.Replace("'", "''")}'"
            : "";

        // Weighted BFS: score = path_score * edge_weight * kind_boost
        var sql = $"""
            WITH RECURSIVE walk(id, depth, score) AS (
                SELECT id, 0, 1.0 FROM Nodes WHERE id IN ({inClause})
                UNION ALL
                SELECT e.dst, w.depth + 1,
                       w.score * COALESCE(e.weight, 1.0) *
                       CASE n.kind
                           WHEN 'method' THEN 1.4 WHEN 'function' THEN 1.4
                           WHEN 'class' THEN 1.3 WHEN 'interface' THEN 1.2
                           ELSE 1.0
                       END
                FROM walk w
                JOIN Edges e ON e.src = w.id {relFilter}
                JOIN Nodes n ON n.id = e.dst
                WHERE w.depth < @maxDepth
                UNION ALL
                SELECT e.src, w.depth + 1,
                       w.score * COALESCE(e.weight, 1.0) *
                       CASE n.kind
                           WHEN 'method' THEN 1.4 WHEN 'function' THEN 1.4
                           WHEN 'class' THEN 1.3 WHEN 'interface' THEN 1.2
                           ELSE 1.0
                       END
                FROM walk w
                JOIN Edges e ON e.dst = w.id {relFilter}
                JOIN Nodes n ON n.id = e.src
                WHERE w.depth < @maxDepth
            )
            SELECT DISTINCT n.*
            FROM walk w
            JOIN Nodes n ON n.id = w.id
            ORDER BY w.score DESC
            LIMIT @maxNodes;
            """;

        using var cmd = _reader.CreateCommand();
        cmd.CommandText = sql;
        for (int i = 0; i < idList.Count; i++)
            cmd.Parameters.AddWithValue($"@s{i}", idList[i]);
        cmd.Parameters.AddWithValue("@maxDepth", maxDepth);
        cmd.Parameters.AddWithValue("@maxNodes", maxNodes);

        return ReadNodeRows(cmd.ExecuteReader());
    }

    public List<(NodeRow caller, NodeRow callee, string relation)> GetCallChain(
        long functionNodeId, int depth = 2)
    {
        // Same weighted approach for call chains
        var results = new List<(NodeRow, NodeRow, string)>();

        using var cmd = _reader.CreateCommand();
        cmd.CommandText = """
            WITH RECURSIVE chain(id, depth) AS (
                SELECT @start, 0
                UNION ALL
                SELECT e.dst, c.depth + 1
                FROM chain c
                JOIN Edges e ON e.src = c.id AND e.rel = 'CALLS'
                WHERE c.depth < @depth
            )
            SELECT c.id AS caller_id, c.name AS caller_name, c.kind AS caller_kind,
                   callee.id AS callee_id, callee.name AS callee_name, callee.kind AS callee_kind,
                   e.rel, e.weight
            FROM chain c
            JOIN Edges e ON e.src = c.id AND e.rel = 'CALLS'
            JOIN Nodes callee ON callee.id = e.dst
            WHERE c.id != @start;
            """;
        cmd.Parameters.AddWithValue("@start", functionNodeId);
        cmd.Parameters.AddWithValue("@depth", depth);

        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            results.Add((
                new NodeRow { Id = rdr.GetInt64(0), Name = rdr.GetString(1), Kind = rdr.GetString(2) },
                new NodeRow { Id = rdr.GetInt64(3), Name = rdr.GetString(4), Kind = rdr.GetString(5) },
                rdr.GetString(6)
            ));
        }
        return results;
    }

    // ═══════════════════════════════════════════
    //  Meta
    // ═══════════════════════════════════════════

    public void SetMeta(string key, string value)
        => WriteLockVoid("INSERT OR REPLACE INTO Meta(key, value) VALUES (@key, @value);",
            cmd => { cmd.Parameters.AddWithValue("@key", key); cmd.Parameters.AddWithValue("@value", value); });

    public string? GetMeta(string key)
    {
        using var cmd = _reader.CreateCommand();
        cmd.CommandText = "SELECT value FROM Meta WHERE key = @key;";
        cmd.Parameters.AddWithValue("@key", key);
        return cmd.ExecuteScalar() as string;
    }

    // ═══════════════════════════════════════════
    //  Maintenance
    // ═══════════════════════════════════════════

    public (int pruned, long beforeBytes, long afterBytes) RunMaintenance(
        string rootDir, TimeSpan? timeToLive = null)
    {
        var before = new FileInfo(_dbPath).Length;
        var pruned = 0;

        if (timeToLive.HasValue)
        {
            var cutoff = DateTime.UtcNow - timeToLive.Value;
            pruned += PruneBefore(cutoff);
        }

        Compact();

        // Invalidate all cached results after maintenance
        _resultCache.Compact(1.0);

        var after = new FileInfo(_dbPath).Length;
        return (pruned, before, after);
    }

    public int PruneBefore(DateTime cutoff)
    {
        var cutoffStr = cutoff.ToString("O");
        var count = WriteLock(cmd => (int)(long)cmd.ExecuteScalar()!,
            "SELECT COUNT(*) FROM Nodes WHERE updated_at < @cutoff;",
            cmd => cmd.Parameters.AddWithValue("@cutoff", cutoffStr));

        if (count > 0)
        {
            WriteLockVoid("DELETE FROM Nodes WHERE updated_at < @cutoff;",
                cmd => cmd.Parameters.AddWithValue("@cutoff", cutoffStr));
        }
        return count;
    }

    public void OptimizeFts()
    {
        WriteLockVoid("INSERT INTO FTS_Index(FTS_Index) VALUES('optimize');");
    }

    public int RebuildFts()
    {
        return WriteLock(cmd =>
        {
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT COUNT(*) FROM FTS_Index;";
            return (int)(long)cmd.ExecuteScalar()!;
        }, "DELETE FROM FTS_Index; INSERT INTO FTS_Index SELECT text, node_id, kind FROM Docs JOIN Nodes ON Nodes.id = Docs.node_id;");
    }

    public void Compact()
    {
        _writeLock.Wait();
        try
        {
            using var vacCmd = _writer.CreateCommand();
            vacCmd.CommandText = "VACUUM;";
            vacCmd.ExecuteNonQuery();
            using var optCmd = _writer.CreateCommand();
            optCmd.CommandText = "PRAGMA optimize;";
            optCmd.ExecuteNonQuery();
            OptimizeFts();
        }
        finally { _writeLock.Release(); }
    }

    // ═══════════════════════════════════════════
    //  Stats
    // ═══════════════════════════════════════════

    public string Stats()
    {
        var n = NodeCount();
        var e = CountEdges();
        var d = CountDocs();
        var kinds = GetKinds();
        // Clear cache to read fresh stats
        var info = new FileInfo(_dbPath);

        return $"Nodes: {n}, Edges: {e}, Docs: {d}, Size: {FormatBytes(info.Length)}\n"
             + $"Kinds: {string.Join(", ", kinds)}";
    }

    private long CountEdges()
    {
        var cmd = GetPreparedRead("SELECT COUNT(*) FROM Edges;");
        return (long)cmd.ExecuteScalar()!;
    }

    private long CountDocs()
    {
        var cmd = GetPreparedRead("SELECT COUNT(*) FROM Docs;");
        return (long)cmd.ExecuteScalar()!;
    }

    private List<string> GetKinds()
    {
        using var cmd = _reader.CreateCommand();
        cmd.CommandText = "SELECT kind, COUNT(*) FROM Nodes GROUP BY kind ORDER BY COUNT(*) DESC;";
        var kinds = new List<string>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
            kinds.Add($"{rdr.GetString(0)}: {rdr.GetInt32(1)}");
        return kinds;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024}KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1}MB"
    };

    // ═══════════════════════════════════════════
    //  Reader helpers
    // ═══════════════════════════════════════════

    private static NodeRow? ReadNodeRow(SqliteDataReader rdr)
    {
        if (!rdr.Read()) return null;
        return MapNodeRow(rdr);
    }

    private static List<NodeRow> ReadNodeRows(SqliteDataReader rdr)
    {
        var results = new List<NodeRow>();
        while (rdr.Read())
            results.Add(MapNodeRow(rdr));
        return results;
    }

    private static NodeRow MapNodeRow(SqliteDataReader rdr) => new()
    {
        Id = rdr.GetInt64(rdr.GetOrdinal("id")),
        ExtId = rdr.IsDBNull(rdr.GetOrdinal("ext_id")) ? null : rdr.GetString(rdr.GetOrdinal("ext_id")),
        Kind = rdr.GetString(rdr.GetOrdinal("kind")),
        Name = rdr.GetString(rdr.GetOrdinal("name")),
        Namespace = rdr.IsDBNull(rdr.GetOrdinal("namespace")) ? null : rdr.GetString(rdr.GetOrdinal("namespace")),
        Signature = rdr.IsDBNull(rdr.GetOrdinal("signature")) ? null : rdr.GetString(rdr.GetOrdinal("signature")),
        Source = rdr.IsDBNull(rdr.GetOrdinal("source")) ? null : rdr.GetString(rdr.GetOrdinal("source")),
        Props = rdr.IsDBNull(rdr.GetOrdinal("props")) ? null : rdr.GetString(rdr.GetOrdinal("props")),
        CreatedAt = rdr.GetString(rdr.GetOrdinal("created_at")),
        UpdatedAt = rdr.GetString(rdr.GetOrdinal("updated_at")),
    };

    private static List<EdgeRow> ReadEdgeRows(SqliteDataReader rdr)
    {
        var results = new List<EdgeRow>();
        while (rdr.Read())
        {
            results.Add(new EdgeRow
            {
                Id = rdr.GetInt64(0),
                Src = rdr.GetInt64(1),
                Dst = rdr.GetInt64(2),
                Relation = rdr.GetString(3),
                Weight = rdr.GetDouble(4),
                Props = rdr.IsDBNull(5) ? null : rdr.GetString(5),
            });
        }
        return results;
    }

    private static List<DocRow> ReadDocRows(SqliteDataReader rdr)
    {
        var results = new List<DocRow>();
        while (rdr.Read())
        {
            results.Add(new DocRow
            {
                Id = rdr.GetInt64(0),
                NodeId = rdr.GetInt64(1),
                Text = rdr.GetString(2),
                Lang = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                Source = rdr.IsDBNull(4) ? null : rdr.GetString(4),
            });
        }
        return results;
    }

    private static List<(long nodeId, string text, double rank, string kind)> ReadFtsResults(SqliteDataReader rdr)
    {
        var results = new List<(long, string, double, string)>();
        while (rdr.Read())
        {
            results.Add((
                rdr.GetInt64(0),
                rdr.GetString(1),
                rdr.GetDouble(2),
                rdr.GetString(3)
            ));
        }
        return results;
    }

    // ═══════════════════════════════════════════
    //  Vector Search (in-memory cosine similarity)
    //  384-dim multilingual MiniLM embedding stored as BLOB (1536 bytes).
    //  Linear scan is fast enough for ≤100K nodes.
    // ═══════════════════════════════════════════

    /// <summary>Insert or update a vector embedding for a node.</summary>
    public void InsertVector(long nodeId, float[] embedding)
    {
        if (embedding.Length != 384)
            throw new ArgumentException($"MiniLM requires 384-dim vectors, got {embedding.Length}");

        var blob = new byte[embedding.Length * 4];
        Buffer.BlockCopy(embedding, 0, blob, 0, blob.Length);

        WriteLockVoid(SQL_INSERT_VEC, cmd =>
        {
            cmd.Parameters.AddWithValue("@nid", nodeId);
            cmd.Parameters.AddWithValue("@vec", blob);
        });
    }

    /// <summary>
    /// Vector similarity search by cosine distance (in-memory, linear scan).
    /// Returns (nodeId, distance) sorted closest-first.
    /// </summary>
    public List<(long nodeId, double distance)> SearchVector(float[] query, int topN = 30)
    {
        if (query.Length != 384)
            throw new ArgumentException($"MiniLM requires 384-dim vectors, got {query.Length}");

        // Read all vectors from the table
        var candidates = new List<(long nodeId, float[] vec)>();
        using (var cmd = _reader.CreateCommand())
        {
            cmd.CommandText = "SELECT node_id, vec FROM VecNodes;";
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                var nid = rdr.GetInt64(0);
                var blob = (byte[])rdr["vec"];
                var vec = new float[384];
                Buffer.BlockCopy(blob, 0, vec, 0, blob.Length);
                candidates.Add((nid, vec));
            }
        }

        // Compute cosine similarity, sort, take topN
        var scored = new List<(long nodeId, double dist)>(candidates.Count);
        foreach (var (nid, vec) in candidates)
        {
            var sim = CosineSimilarity(query, vec);
            scored.Add((nid, 1.0 - sim)); // distance = 1 - similarity
        }

        scored.Sort((a, b) => a.dist.CompareTo(b.dist));
        return scored.Take(topN).ToList();
    }

    /// <summary>Delete the vector embedding for a node.</summary>
    public void DeleteVector(long nodeId)
    {
        WriteLockVoid(SQL_DELETE_VEC, cmd =>
        {
            cmd.Parameters.AddWithValue("@nid", nodeId);
        });
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        var norm = Math.Sqrt(na) * Math.Sqrt(nb);
        return norm > 1e-12 ? dot / norm : 0;
    }

    private const string SQL_INSERT_VEC =
        "INSERT OR REPLACE INTO VecNodes(node_id, vec) VALUES (@nid, @vec);";

    private const string SQL_DELETE_VEC =
        "DELETE FROM VecNodes WHERE node_id = @nid;";

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    // ═══════════════════════════════════════════
    //  Schema initialization
    // ═══════════════════════════════════════════

    private void InitSchema()
    {
        ExecuteOnWriter("""
            CREATE TABLE IF NOT EXISTS Meta (
                key   TEXT PRIMARY KEY,
                value TEXT
            );

            CREATE TABLE IF NOT EXISTS Nodes (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                ext_id     TEXT UNIQUE,
                kind       TEXT NOT NULL DEFAULT '',
                name       TEXT NOT NULL DEFAULT '',
                namespace  TEXT,
                signature  TEXT,
                source     TEXT,
                props      TEXT,
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ','now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ','now'))
            );
            CREATE INDEX IF NOT EXISTS idx_nodes_kind ON Nodes(kind);
            CREATE INDEX IF NOT EXISTS idx_nodes_name ON Nodes(name);
            CREATE INDEX IF NOT EXISTS idx_nodes_source ON Nodes(source);

            CREATE TABLE IF NOT EXISTS Edges (
                id     INTEGER PRIMARY KEY AUTOINCREMENT,
                src    INTEGER NOT NULL REFERENCES Nodes(id) ON DELETE CASCADE,
                dst    INTEGER NOT NULL REFERENCES Nodes(id) ON DELETE CASCADE,
                rel    TEXT NOT NULL DEFAULT '',
                weight REAL NOT NULL DEFAULT 1.0,
                props  TEXT,
                UNIQUE(src, dst, rel)
            );
            CREATE INDEX IF NOT EXISTS idx_edges_src ON Edges(src);
            CREATE INDEX IF NOT EXISTS idx_edges_dst ON Edges(dst);
            CREATE INDEX IF NOT EXISTS idx_edges_rel ON Edges(rel);

            CREATE TABLE IF NOT EXISTS Docs (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                node_id INTEGER NOT NULL REFERENCES Nodes(id) ON DELETE CASCADE,
                text    TEXT NOT NULL DEFAULT '',
                lang    TEXT,
                source  TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_docs_node ON Docs(node_id);

            CREATE VIRTUAL TABLE IF NOT EXISTS FTS_Index USING fts5(
                text,
                node_id UNINDEXED,
                kind UNINDEXED,
                tokenize="porter unicode61 remove_diacritics 1"
            );

            -- Triggers: auto-sync Docs → FTS_Index
            CREATE TRIGGER IF NOT EXISTS docs_ai AFTER INSERT ON Docs BEGIN
                INSERT INTO FTS_Index(text, node_id, kind)
                SELECT new.text, new.node_id, n.kind
                FROM Nodes n WHERE n.id = new.node_id;
            END;

            CREATE TRIGGER IF NOT EXISTS docs_ad AFTER DELETE ON Docs BEGIN
                INSERT INTO FTS_Index(FTS_Index, rowid, text, node_id, kind)
                VALUES ('delete', old.rowid, old.text, old.node_id, '');
            END;

            CREATE TRIGGER IF NOT EXISTS docs_au AFTER UPDATE ON Docs BEGIN
                INSERT INTO FTS_Index(FTS_Index, rowid, text, node_id, kind)
                VALUES ('delete', old.rowid, old.text, old.node_id, '');
                INSERT INTO FTS_Index(text, node_id, kind)
                SELECT new.text, new.node_id, n.kind
                FROM Nodes n WHERE n.id = new.node_id;
            END;

            -- Vector embeddings (BLOB: 384 float32 = 1536 bytes, MiniLM multilingual)
            CREATE TABLE IF NOT EXISTS VecNodes (
                node_id   INTEGER PRIMARY KEY REFERENCES Nodes(id) ON DELETE CASCADE,
                vec       BLOB NOT NULL,
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ','now'))
            );
            """);
    }

    private void ExecuteOnWriter(string sql)
    {
        _writeLock.Wait();
        try
        {
            using var cmd = _writer.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        finally { _writeLock.Release(); }
    }

    // ═══════════════════════════════════════════
    //  Dispose
    // ═══════════════════════════════════════════

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var cmd in _writeCmdCache.Values) cmd.Dispose();
        foreach (var cmd in _readCmdCache.Values) cmd.Dispose();
        _writeCmdCache.Clear();
        _readCmdCache.Clear();
        _resultCache.Dispose();

        _writer.Dispose();
        _reader.Dispose();
        _writeLock.Dispose();
    }
}

// ═══════════════════════════════════════════════
//  JSON serializer options
// ═══════════════════════════════════════════════

internal static partial class KgStoreInternals
{
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };
}

// ═══════════════════════════════════════════════
//  Data Transfer Objects
// ═══════════════════════════════════════════════

public sealed class NodeRow
{
    public long Id { get; set; }
    public string? ExtId { get; set; }
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Namespace { get; set; }
    public string? Signature { get; set; }
    public string? Source { get; set; }
    public string? Props { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";

    public Dictionary<string, object?>? GetProps()
    {
        if (string.IsNullOrEmpty(Props)) return null;
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(Props, KgStoreInternals.JsonOpts);
    }

    public override string ToString() => $"[{Kind}] {Name} ({Namespace})";
}

public sealed class EdgeRow
{
    public long Id { get; set; }
    public long Src { get; set; }
    public long Dst { get; set; }
    public string Relation { get; set; } = "";
    public double Weight { get; set; }
    public string? Props { get; set; }

    public override string ToString() => $"{Src} --[{Relation}]--> {Dst}";
}

public sealed class DocRow
{
    public long Id { get; set; }
    public long NodeId { get; set; }
    public string Text { get; set; } = "";
    public string? Lang { get; set; }
    public string? Source { get; set; }

    public override string ToString()
    {
        var snippet = Text.Length > 60 ? Text[..60] + "..." : Text;
        return $"Doc({Id}) for Node({NodeId}): {snippet}";
    }
}
