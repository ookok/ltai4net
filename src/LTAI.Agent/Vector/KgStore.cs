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
using System.Buffers;
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
    private readonly AsyncLocal<bool> _ownsWriteLock = new();
    private readonly string _dbPath;
    private bool _disposed;
    public string DbPath => _dbPath;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

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
    private int _cacheStamp; // incremented on every write to invalidate SearchFts cache

    // IVF centroids for approximate nearest neighbor search
    private float[][]? _centroids;
    private int _vectorCount;
    private const int IvfNumCentroids = 32;

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
            PRAGMA auto_vacuum=INCREMENTAL;
            PRAGMA mmap_size=268435456;         -- 256MB 内存映射
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=5000;           -- 5s 等待而非立即失败
            PRAGMA temp_store=MEMORY;           -- 排序/索引使用内存
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

    private SqliteCommand GetPreparedRead(string key, string sql)
    {
        ThrowIfDisposed();
        if (_readCmdCache.TryGetValue(key, out var cmd))
        {
            cmd.Parameters.Clear();
            return cmd;
        }

        cmd = _reader.CreateCommand();
        cmd.CommandText = sql;
        cmd.Prepare();
        _readCmdCache[key] = cmd;
        return cmd;
    }

    /// <summary>Execute a write command with exclusive async lock (reentrant-safe).</summary>
    private async Task<T> WriteLockAsync<T>(Func<SqliteCommand, T> action, string sql,
        Action<SqliteCommand>? bindParams = null)
    {
        ThrowIfDisposed();
        bool acquired = false;
        if (!_ownsWriteLock.Value)
        {
            await _writeLock.WaitAsync().ConfigureAwait(false);
            _ownsWriteLock.Value = true;
            acquired = true;
        }
        try
        {
            var cmd = GetPreparedWrite(sql);
            cmd.Parameters.Clear();
            bindParams?.Invoke(cmd);
            return action(cmd);
        }
        finally { if (acquired) { _ownsWriteLock.Value = false; _writeLock.Release(); Interlocked.Increment(ref _cacheStamp); } }
    }

    private async Task WriteLockVoidAsync(string sql, Action<SqliteCommand>? bindParams = null)
    {
        ThrowIfDisposed();
        bool acquired = false;
        if (!_ownsWriteLock.Value)
        {
            await _writeLock.WaitAsync().ConfigureAwait(false);
            _ownsWriteLock.Value = true;
            acquired = true;
        }
        try
        {
            var cmd = GetPreparedWrite(sql);
            cmd.Parameters.Clear();
            bindParams?.Invoke(cmd);
            cmd.ExecuteNonQuery();
        }
        finally { if (acquired) { _ownsWriteLock.Value = false; _writeLock.Release(); Interlocked.Increment(ref _cacheStamp); } }
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

    public async Task<long> UpsertNode(string extId, string kind, string name,
        string? ns = null, string? signature = null,
        string? source = null, Dictionary<string, object?>? props = null)
    {
        var propsJson = props != null ? JsonSerializer.Serialize(props, KgStoreInternals.JsonOpts) : null;
        return await WriteLockAsync(cmd =>
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
        var cmd = GetPreparedRead(SQL_GET_NODE, SQL_GET_NODE);
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@id", id);
        return ReadNodeRow(cmd.ExecuteReader());
    }

    private const string SQL_GET_NODE_BY_EXT = "SELECT * FROM Nodes WHERE ext_id = @ext_id;";

    public NodeRow? GetNodeByExtId(string extId)
    {
        var cmd = GetPreparedRead(SQL_GET_NODE_BY_EXT, SQL_GET_NODE_BY_EXT);
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@ext_id", extId);
        return ReadNodeRow(cmd.ExecuteReader());
    }

    private const string SQL_GET_NODES_BY_KIND = "SELECT * FROM Nodes WHERE kind = @kind ORDER BY name;";

    public List<NodeRow> GetNodesByKind(string kind)
    {
        var cmd = GetPreparedRead(SQL_GET_NODES_BY_KIND, SQL_GET_NODES_BY_KIND);
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@kind", kind);
        return ReadNodeRows(cmd.ExecuteReader());
    }

    private const string SQL_GET_NODES_BY_SOURCE = "SELECT * FROM Nodes WHERE source = @src ORDER BY name;";

    public List<NodeRow> GetNodesBySource(string source)
    {
        var cmd = GetPreparedRead(SQL_GET_NODES_BY_SOURCE, SQL_GET_NODES_BY_SOURCE);
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@src", source);
        return ReadNodeRows(cmd.ExecuteReader());
    }

    public List<NodeRow> SearchNodesByName(string namePattern, int limit = 20)
    {
        ThrowIfDisposed();
        using var cmd = _reader.CreateCommand();
        cmd.CommandText = "SELECT * FROM Nodes WHERE name LIKE @pat ORDER BY kind, name LIMIT @lim;";
        cmd.Parameters.AddWithValue("@pat", $"%{EscapeLike(namePattern)}%");
        cmd.Parameters.AddWithValue("@lim", limit);
        return ReadNodeRows(cmd.ExecuteReader());
    }

    private const string SQL_DELETE_NODE = "DELETE FROM Nodes WHERE id = @id;";

    public async Task<bool> DeleteNode(long id)
    {
        var deleted = await WriteLockAsync(cmd => cmd.ExecuteNonQuery() > 0, SQL_DELETE_NODE,
            cmd => cmd.Parameters.AddWithValue("@id", id));
        if (deleted) await IncrementalVacuumAsync(50);
        return deleted;
    }

    private const string SQL_DELETE_SOURCE = "DELETE FROM Nodes WHERE source = @src;";

    public async Task<int> DeleteSource(string source)
    {
        var count = await WriteLockAsync(cmd => cmd.ExecuteNonQuery(), SQL_DELETE_SOURCE,
            cmd => cmd.Parameters.AddWithValue("@src", source));
        if (count > 0) await IncrementalVacuumAsync(200);
        return count;
    }

    private async Task IncrementalVacuumAsync(int pages)
    {
        if (_disposed) return;
        await WriteLockVoidAsync($"PRAGMA incremental_vacuum({pages});");
    }

    private const string SQL_NODE_COUNT = "SELECT COUNT(*) FROM Nodes;";

    public long NodeCount()
    {
        var cmd = GetPreparedRead(SQL_NODE_COUNT, SQL_NODE_COUNT);
        cmd.Parameters.Clear();
        return (long)cmd.ExecuteScalar()!;
    }

    // ═══════════════════════════════════════════
    //  Edges
    // ═══════════════════════════════════════════

    private const string SQL_ADD_EDGE = """
        INSERT OR IGNORE INTO Edges(src, dst, rel, weight, props)
        VALUES (@src, @dst, @rel, @weight, @props);
        """;

    public async Task AddEdge(long srcId, long dstId, string relation, double weight = 1.0,
        Dictionary<string, object?>? props = null)
    {
        var propsJson = props != null ? JsonSerializer.Serialize(props, KgStoreInternals.JsonOpts) : null;
        await WriteLockVoidAsync(SQL_ADD_EDGE, cmd =>
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
        ThrowIfDisposed();
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

    public async Task<int> DeleteEdges(long nodeId, string? relation = null)
    {
        var sql = new StringBuilder("DELETE FROM Edges WHERE src = @nid OR dst = @nid");
        if (relation != null) sql.Append(" AND rel = @rel");
        return await WriteLockAsync(cmd => cmd.ExecuteNonQuery(), sql.ToString(), cmd =>
        {
            cmd.Parameters.AddWithValue("@nid", nodeId);
            if (relation != null) cmd.Parameters.AddWithValue("@rel", relation);
        });
    }

    // ═══════════════════════════════════════════
    //  Docs
    // ═══════════════════════════════════════════

    /// <summary>
    /// Split text into chunks for large documents.
    /// Each chunk is at most ~chunkLines lines (roughly 5000–8000 chars).
    /// Returns at least one chunk even for empty text.
    /// </summary>
    private static List<string> ChunkText(string text, int chunkLines = 200)
    {
        if (string.IsNullOrEmpty(text)) return [""];
        if (chunkLines <= 0) return [text];

        var lines = text.Split('\n');
        if (lines.Length <= chunkLines) return [text];

        var chunks = new List<string>(1 + lines.Length / chunkLines);
        for (int i = 0; i < lines.Length; i += chunkLines)
        {
            var end = Math.Min(i + chunkLines, lines.Length);
            chunks.Add(string.Join('\n', lines[i..end]));
        }
        return chunks;
    }

    private const string SQL_ADD_DOC = """
        INSERT INTO Docs(node_id, text, lang, source)
        VALUES (@nid, @text, @lang, @src);
        SELECT last_insert_rowid();
        """;

    public async Task<long> AddDoc(long nodeId, string text, string? lang = "code", string? source = null)
    {
        var chunks = ChunkText(text);
        long lastId = 0;
        foreach (var chunk in chunks)
        {
            lastId = await WriteLockAsync(cmd => (long)cmd.ExecuteScalar()!, SQL_ADD_DOC, cmd =>
            {
                cmd.Parameters.AddWithValue("@nid", nodeId);
                cmd.Parameters.AddWithValue("@text", chunk);
                cmd.Parameters.AddWithValue("@lang", (object?)lang ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@src", (object?)source ?? DBNull.Value);
            });
        }
        return lastId;
    }

    public async Task ReplaceDocsAsync(long nodeId, List<(string text, string? lang, string? source)> docs)
    {
        ThrowIfDisposed();
        bool acquired = false;
        if (!_ownsWriteLock.Value)
        {
            await _writeLock.WaitAsync();
            _ownsWriteLock.Value = true;
            acquired = true;
        }
        try
        {
            using var tx = _writer.BeginTransaction();
            using var del = GetPreparedWrite("DELETE FROM Docs WHERE node_id = @nid;");
            del.Parameters.Clear();
            del.Parameters.AddWithValue("@nid", nodeId);
            del.ExecuteNonQuery();

            foreach (var (text, lang, src) in docs)
            {
                var chunks = ChunkText(text);
                foreach (var chunk in chunks)
                {
                    using var ins = _writer.CreateCommand();
                    ins.CommandText = "INSERT INTO Docs(node_id, text, lang, source) VALUES (@nid, @text, @lang, @src);";
                    ins.Parameters.AddWithValue("@nid", nodeId);
                    ins.Parameters.AddWithValue("@text", chunk);
                    ins.Parameters.AddWithValue("@lang", (object?)lang ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@src", (object?)src ?? DBNull.Value);
                    ins.ExecuteNonQuery();
                }
            }
            tx.Commit();
            _resultCache.Compact(0.5);
        }
        finally { if (acquired) { _ownsWriteLock.Value = false; _writeLock.Release(); } }
    }

    /// <summary>
    /// Acquire the write lock once, wrap work in a single transaction,
    /// compact result cache on commit.  Reentrant-safe: if call-site
    /// already owns the lock the outer transaction is reused.
    /// </summary>
    public async Task ExecuteInTransactionAsync(Func<Task> work)
    {
        ThrowIfDisposed();
        bool acquired = false;
        if (!_ownsWriteLock.Value)
        {
            await _writeLock.WaitAsync();
            _ownsWriteLock.Value = true;
            acquired = true;
        }
        try
        {
            using var tx = _writer.BeginTransaction();
            await work();
            tx.Commit();
            Interlocked.Increment(ref _cacheStamp);
        }
        finally { if (acquired) { _ownsWriteLock.Value = false; _writeLock.Release(); } }
    }

    private const string SQL_GET_DOCS = "SELECT * FROM Docs WHERE node_id = @nid ORDER BY id;";

    public List<DocRow> GetDocs(long nodeId)
    {
        var cmd = GetPreparedRead(SQL_GET_DOCS, SQL_GET_DOCS);
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

    /// <summary>
    /// Characters that produce FTS5 syntax errors when used literally in MATCH.
    /// Preserves ^ (prefix boost), * (wildcard), " (phrase), + - ~ : for power users.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex Fts5SpecialChars =
        new(@"[()@]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Sanitize a query for FTS5 MATCH: remove only characters that cause
    /// unresolvable syntax errors (unbalanced parens, bare @). Preserves
    /// valid FTS5 operators: ^ * " + - ~ :
    /// </summary>
    private static string SanitizeFts5Query(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return query;

        var sanitized = Fts5SpecialChars.Replace(query, " ");
        sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, @"\s+", " ").Trim();

        // Cap query length so that 100 KB paste doesn't choke FTS5
        const int MaxQueryLength = 500;
        if (sanitized.Length > MaxQueryLength)
            sanitized = sanitized[..MaxQueryLength];

        return sanitized.Length > 0 ? sanitized : query;
    }

    public List<(long nodeId, string text, double rank, string kind)> SearchFts(
        string query, int topN = 30, string? kindFilter = null)
    {
        // Sanitize FTS5 query to prevent syntax errors (e.g. "@" in email/username)
        query = SanitizeFts5Query(query);
        if (string.IsNullOrWhiteSpace(query))
            return [];

        // Check result cache first (stamp invalidates on any write)
        var cacheKey = $"fts:{query}:{topN}:{kindFilter}:{_cacheStamp}";
        if (_resultCache.TryGetValue(cacheKey, out List<(long, string, double, string)>? cached))
            return cached!;

        var cmd = GetPreparedRead(SQL_SEARCH_FTS, SQL_SEARCH_FTS);
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
        ThrowIfDisposed();
        maxDepth = Math.Min(maxDepth, MaxTraversalDepth);
        maxNodes = Math.Min(maxNodes, MaxTraversalNodes);

        var idList = startIds.Take(10).ToList();
        if (idList.Count == 0) return [];

        var inClause = string.Join(",", idList.Select((_, i) => $"@s{i}"));
        var relClause = relation != null ? "AND e.rel = @rel" : "";

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
                JOIN Edges e ON e.src = w.id {relClause}
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
                JOIN Edges e ON e.dst = w.id {relClause}
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
        if (relation != null)
            cmd.Parameters.AddWithValue("@rel", relation);

        return ReadNodeRows(cmd.ExecuteReader());
    }

    public List<(NodeRow caller, NodeRow callee, string relation)> GetCallChain(
        long functionNodeId, int depth = 2)
    {
        ThrowIfDisposed();

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

    public async Task SetMeta(string key, string value)
        => await WriteLockVoidAsync("INSERT OR REPLACE INTO Meta(key, value) VALUES (@key, @value);",
            cmd => { cmd.Parameters.AddWithValue("@key", key); cmd.Parameters.AddWithValue("@value", value); });

    public string? GetMeta(string key)
    {
        ThrowIfDisposed();
        using var cmd = _reader.CreateCommand();
        cmd.CommandText = "SELECT value FROM Meta WHERE key = @key;";
        cmd.Parameters.AddWithValue("@key", key);
        return cmd.ExecuteScalar() as string;
    }

    // ═══════════════════════════════════════════
    //  Maintenance
    // ═══════════════════════════════════════════

    public async Task<(int pruned, long beforeBytes, long afterBytes)> RunMaintenanceAsync(
        string rootDir, TimeSpan? timeToLive = null)
    {
        var before = new FileInfo(_dbPath).Length;
        var pruned = 0;

        if (timeToLive.HasValue)
        {
            var cutoff = DateTime.UtcNow - timeToLive.Value;
            pruned += await PruneBefore(cutoff);
        }

        if (pruned > 0)
            await CompactAsync();

        // Invalidate all cached results after maintenance
        _resultCache.Compact(1.0);

        var after = new FileInfo(_dbPath).Length;
        return (pruned, before, after);
    }

    public async Task<int> PruneBefore(DateTime cutoff)
    {
        var cutoffStr = cutoff.ToString("O");
        var count = await WriteLockAsync(cmd => (int)(long)cmd.ExecuteScalar()!,
            "SELECT COUNT(*) FROM Nodes WHERE updated_at < @cutoff;",
            cmd => cmd.Parameters.AddWithValue("@cutoff", cutoffStr));

        if (count > 0)
        {
            await WriteLockVoidAsync("DELETE FROM Nodes WHERE updated_at < @cutoff;",
                cmd => cmd.Parameters.AddWithValue("@cutoff", cutoffStr));
        }
        return count;
    }

    public async Task OptimizeFtsAsync()
    {
        await WriteLockVoidAsync("INSERT INTO FTS_Index(FTS_Index) VALUES('optimize');");
    }

    public async Task<int> RebuildFtsAsync()
    {
        return await WriteLockAsync(cmd =>
        {
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT COUNT(*) FROM FTS_Index;";
            return (int)(long)cmd.ExecuteScalar()!;
        }, "DELETE FROM FTS_Index; INSERT INTO FTS_Index SELECT text, node_id, kind FROM Docs JOIN Nodes ON Nodes.id = Docs.node_id;");
    }

    public async Task CompactAsync()
    {
        ThrowIfDisposed();
        bool acquired = false;
        if (!_ownsWriteLock.Value)
        {
            await _writeLock.WaitAsync();
            _ownsWriteLock.Value = true;
            acquired = true;
        }
        try
        {
            using var vacCmd = _writer.CreateCommand();
            vacCmd.CommandText = "VACUUM;";
            await vacCmd.ExecuteNonQueryAsync();
            using var optCmd = _writer.CreateCommand();
            optCmd.CommandText = "PRAGMA optimize;";
            await optCmd.ExecuteNonQueryAsync();
            await OptimizeFtsAsync();
            _resultCache.Compact(0.5);
        }
        finally { if (acquired) { _ownsWriteLock.Value = false; _writeLock.Release(); } }
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
        const string sql = "SELECT COUNT(*) FROM Edges;";
        var cmd = GetPreparedRead(sql, sql);
        cmd.Parameters.Clear();
        return (long)cmd.ExecuteScalar()!;
    }

    private long CountDocs()
    {
        const string sql = "SELECT COUNT(*) FROM Docs;";
        var cmd = GetPreparedRead(sql, sql);
        cmd.Parameters.Clear();
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
    public async Task InsertVectorAsync(long nodeId, float[] embedding)
    {
        if (embedding.Length != 384)
            throw new ArgumentException($"MiniLM requires 384-dim vectors, got {embedding.Length}");

        var blob = new byte[embedding.Length * 4];
        Buffer.BlockCopy(embedding, 0, blob, 0, blob.Length);

        int centroidId = FindNearestCentroidIndex(embedding);

        await WriteLockVoidAsync(SQL_INSERT_VEC, cmd =>
        {
            cmd.Parameters.AddWithValue("@nid", nodeId);
            cmd.Parameters.AddWithValue("@vec", blob);
            cmd.Parameters.AddWithValue("@cid", centroidId);
        });

        Interlocked.Increment(ref _vectorCount);
    }

    /// <summary>
    /// Vector similarity search by cosine distance.
    /// Uses IVF centroids (if available) to limit the scan to the nearest cluster(s),
    /// falling back to full linear scan when centroids are not yet built.
    /// </summary>
    public List<(long nodeId, float distance)> SearchVector(float[] query, int topN = 30, string? kindFilter = null)
    {
        ThrowIfDisposed();
        if (query.Length != 384)
            throw new ArgumentException($"MiniLM requires 384-dim vectors, got {query.Length}");

        var cents = LoadCentroids();
        bool useIvf = cents.Length > 0;

        List<(long nodeId, float[] vec)> candidates;
        if (useIvf)
        {
            // Find nprobe nearest centroids to the query
            var centScores = new List<(int idx, float sim)>();
            for (int i = 0; i < cents.Length; i++)
                centScores.Add((i, CosineSimilarity(query, cents[i])));
            centScores.Sort((a, b) => b.sim.CompareTo(a.sim));

            int nprobe = Math.Min(3, centScores.Count);
            var probeIds = centScores.Take(nprobe).Select(c => c.idx).ToList();
            probeIds.Add(-1); // always include unassigned vectors

            // Build dynamic IN clause
            var inClause = string.Join(",", probeIds.Select((_, i) => $"@c{i}"));
            var sql = kindFilter != null
                ? $"SELECT v.node_id, v.vec FROM VecNodes v JOIN Nodes n ON n.id = v.node_id WHERE v.centroid_id IN ({inClause}) AND n.kind = @kind;"
                : $"SELECT v.node_id, v.vec FROM VecNodes v WHERE v.centroid_id IN ({inClause});";

            candidates = [];
            using (var cmd = _reader.CreateCommand())
            {
                cmd.CommandText = sql;
                for (int i = 0; i < probeIds.Count; i++)
                    cmd.Parameters.AddWithValue($"@c{i}", probeIds[i]);
                if (kindFilter != null)
                    cmd.Parameters.AddWithValue("@kind", kindFilter);
                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    var nid = rdr.GetInt64(0);
                    var blob = (byte[])rdr["vec"];
                    var vec = System.Buffers.ArrayPool<float>.Shared.Rent(384);
                    Buffer.BlockCopy(blob, 0, vec, 0, blob.Length);
                    candidates.Add((nid, vec));
                }
            }
        }
        else
        {
            // Fallback: full linear scan
            candidates = [];
            using (var cmd = _reader.CreateCommand())
            {
                cmd.CommandText = kindFilter != null
                    ? "SELECT v.node_id, v.vec FROM VecNodes v JOIN Nodes n ON n.id = v.node_id WHERE n.kind = @kind;"
                    : "SELECT node_id, vec FROM VecNodes;";
                if (kindFilter != null)
                    cmd.Parameters.AddWithValue("@kind", kindFilter);
                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    var nid = rdr.GetInt64(0);
                    var blob = (byte[])rdr["vec"];
                    var vec = System.Buffers.ArrayPool<float>.Shared.Rent(384);
                    Buffer.BlockCopy(blob, 0, vec, 0, blob.Length);
                    candidates.Add((nid, vec));
                }
            }
        }

        // Compute cosine similarity with early termination.
        // Once topN results have distance < 0.05 (similarity > 0.95),
        // stop scanning — remaining candidates are unlikely to beat them.
        var scored = new List<(long nodeId, float dist)>(Math.Min(candidates.Count, 50000));
        var goodCount = 0;
        const float earlyThreshold = 0.05f;

        foreach (var (nid, vec) in candidates)
        {
            var sim = CosineSimilarity(query, vec);
            var dist = 1f - sim;
            scored.Add((nid, dist));
            if (dist < earlyThreshold) goodCount++;

            if (goodCount >= topN) break;
        }

        foreach (var (_, vec) in candidates)
            System.Buffers.ArrayPool<float>.Shared.Return(vec);

        scored.Sort((a, b) => a.dist.CompareTo(b.dist));
        return scored.Take(topN).ToList();
    }

    /// <summary>Delete the vector embedding for a node.</summary>
    public async Task DeleteVectorAsync(long nodeId)
    {
        await WriteLockVoidAsync(SQL_DELETE_VEC, cmd =>
        {
            cmd.Parameters.AddWithValue("@nid", nodeId);
        });
    }

    private static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float dot = 0, normA = 0, normB = 0;
        int i = 0;

        if (System.Numerics.Vector.IsHardwareAccelerated && a.Length >= System.Numerics.Vector<float>.Count)
        {
            int vecLen = System.Numerics.Vector<float>.Count;
            var aVecs = System.Runtime.InteropServices.MemoryMarshal.Cast<float, System.Numerics.Vector<float>>(a);
            var bVecs = System.Runtime.InteropServices.MemoryMarshal.Cast<float, System.Numerics.Vector<float>>(b);
            var vdot = System.Numerics.Vector<float>.Zero;
            var vna = System.Numerics.Vector<float>.Zero;
            var vnb = System.Numerics.Vector<float>.Zero;
            for (int j = 0; j < aVecs.Length; j++)
            {
                vdot += aVecs[j] * bVecs[j];
                vna += aVecs[j] * aVecs[j];
                vnb += bVecs[j] * bVecs[j];
            }
            for (int k = 0; k < vecLen; k++)
            {
                dot += vdot[k];
                normA += vna[k];
                normB += vnb[k];
            }
            i += aVecs.Length * vecLen;
        }

        for (; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom == 0 ? 0 : dot / denom;
    }

    private const string SQL_INSERT_VEC =
        "INSERT OR REPLACE INTO VecNodes(node_id, vec, centroid_id) VALUES (@nid, @vec, @cid);";

    private const string SQL_DELETE_VEC =
        "DELETE FROM VecNodes WHERE node_id = @nid;";

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    // ═══════════════════════════════════════════
    //  Schema initialization
    // ═══════════════════════════════════════════

    private void InitSchema()
    {
        // 构造函数中执行，无并发风险，直接使用 writer 连接
        using var cmd = _writer.CreateCommand();
        cmd.CommandText = """
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
            """;
        cmd.ExecuteNonQuery();

        // Migration: add centroid_id column for IVF (existing databases)
        try { using var m = _writer.CreateCommand(); m.CommandText = "ALTER TABLE VecNodes ADD COLUMN centroid_id INTEGER DEFAULT -1;"; m.ExecuteNonQuery(); }
        catch { /* column already exists — fine */ }
    }

    /// <summary>
    /// Load centroids from Meta cache (row-major float arrays).
    /// Returns empty array if none exist.
    /// </summary>
    private float[][] LoadCentroids()
    {
        if (_centroids != null) return _centroids;
        var raw = GetMeta("vec:centroids");
        if (string.IsNullOrEmpty(raw)) return [];
        try { _centroids = JsonSerializer.Deserialize<float[][]>(raw, KgStoreInternals.JsonOpts) ?? []; }
        catch { _centroids = []; }
        return _centroids!;
    }

    /// <summary>
    /// Persist centroids to Meta.
    /// </summary>
    private async Task SaveCentroidsAsync(float[][] centroids)
    {
        _centroids = centroids;
        var json = JsonSerializer.Serialize(centroids, KgStoreInternals.JsonOpts);
        await SetMeta("vec:centroids", json);
    }

    /// <summary>
    /// Find the index of the nearest centroid for a given vector.
    /// Returns -1 if no centroids exist.
    /// </summary>
    private int FindNearestCentroidIndex(float[] vec)
    {
        var cents = LoadCentroids();
        if (cents.Length == 0) return -1;
        int best = 0;
        float bestSim = -1f;
        for (int i = 0; i < cents.Length; i++)
        {
            var sim = CosineSimilarity(vec, cents[i]);
            if (sim > bestSim) { bestSim = sim; best = i; }
        }
        return best;
    }

    /// <summary>
    /// Rebuild IVF centroids via k-means++ initialization.
    /// Reads all vectors from VecNodes, clusters into <see cref="IvfNumCentroids"/> centroids,
    /// updates all centroid_id assignments.
    /// </summary>
    public async Task RebuildCentroidsAsync()
    {
        ThrowIfDisposed();
        // Read all vectors (needs to be inside lock for consistency)
        bool acquired = false;
        if (!_ownsWriteLock.Value)
        {
            await _writeLock.WaitAsync();
            _ownsWriteLock.Value = true;
            acquired = true;
        }
        try
        {
            List<(long nodeId, float[] vec)> all = [];
            using (var rdr = _reader.CreateCommand())
            {
                rdr.CommandText = "SELECT v.node_id, v.vec FROM VecNodes v;";
                using var reader = rdr.ExecuteReader();
                while (reader.Read())
                {
                    var nid = reader.GetInt64(0);
                    var blob = (byte[])reader["vec"];
                    var vec = new float[384];
                    Buffer.BlockCopy(blob, 0, vec, 0, blob.Length);
                    all.Add((nid, vec));
                }
            }

            if (all.Count < IvfNumCentroids)
            {
                _centroids = [];
                await SaveCentroidsAsync([]);
                return;
            }

            // k-means++ initialization
            int k = Math.Min(IvfNumCentroids, all.Count / 2);
            var rng = new Random(42);
            var centroids = new float[k][];

            // First centroid: random pick
            centroids[0] = all[rng.Next(all.Count)].vec;

            // Remaining centroids: weighted by distance to nearest existing centroid
            var minDist = new float[all.Count];
            for (int c = 1; c < k; c++)
            {
                float total = 0;
                for (int i = 0; i < all.Count; i++)
                {
                    minDist[i] = 1f - CosineSimilarity(all[i].vec, centroids[c - 1]);
                    if (c > 1) minDist[i] = Math.Min(minDist[i], 1f - CosineSimilarity(all[i].vec, centroids[0])); // approximate
                    total += minDist[i];
                }

                double r = rng.NextDouble() * total;
                for (int i = 0; i < all.Count; i++)
                {
                    r -= minDist[i];
                    if (r <= 0) { centroids[c] = all[i].vec; break; }
                }
            }

            // One round of assignment (not full k-means iteration — good enough)
            var assignments = new int[all.Count];
            for (int i = 0; i < all.Count; i++)
            {
                int best = 0;
                float bestScore = -1f;
                for (int c = 0; c < k; c++)
                {
                    var sim = CosineSimilarity(all[i].vec, centroids[c]);
                    if (sim > bestScore) { bestScore = sim; best = c; }
                }
                assignments[i] = best;
            }

            // Persist centroids
            await SaveCentroidsAsync(centroids);

            // Bulk-update centroid_id in VecNodes
            using var tx = _writer.BeginTransaction();
            using var upd = _writer.CreateCommand();
            upd.CommandText = "UPDATE VecNodes SET centroid_id = @cid WHERE node_id = @nid;";
            var cidParam = upd.Parameters.Add("@cid", SqliteType.Integer);
            var nidParam = upd.Parameters.Add("@nid", SqliteType.Integer);
            upd.Prepare();

            for (int i = 0; i < all.Count; i++)
            {
                cidParam.Value = assignments[i];
                nidParam.Value = all[i].nodeId;
                upd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        finally { if (acquired) { _ownsWriteLock.Value = false; _writeLock.Release(); } }
    }

    private async Task ExecuteOnWriterAsync(string sql)
    {
        ThrowIfDisposed();
        bool acquired = false;
        if (!_ownsWriteLock.Value)
        {
            await _writeLock.WaitAsync();
            _ownsWriteLock.Value = true;
            acquired = true;
        }
        try
        {
            using var cmd = _writer.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        finally { if (acquired) { _ownsWriteLock.Value = false; _writeLock.Release(); } }
    }

    // ═══════════════════════════════════════════
    //  Dispose
    // ═══════════════════════════════════════════

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var cmd in _writeCmdCache.Values) cmd.Dispose();
        _writeCmdCache.Clear();
        foreach (var cmd in _readCmdCache.Values) cmd.Dispose();
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
