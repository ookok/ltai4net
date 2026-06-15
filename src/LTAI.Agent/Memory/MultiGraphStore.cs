using System.Threading;
using Microsoft.Data.Sqlite;

namespace LTAI.Agent.Memory;

public sealed class MultiGraphStore : IDisposable
{
    private readonly string _connectionString;
    private readonly object _gate = new();
    private int _nodeSeq;
    private static readonly TimeSpan CqResetTimeout = TimeSpan.FromMinutes(5);

    public TemporalGraph Temporal { get; }
    public CausalGraph Causal { get; }
    public SemanticGraph Semantic { get; }
    public EntityGraph Entity { get; }
    public IntentRouter IntentRouter { get; }

    // TTL for nodes: auto-expire after this many seconds (default 90 days)
    private const long NodeTtlSeconds = 90L * 24 * 60 * 60;

    public MultiGraphStore(string dbPath, double causalThreshold = 0.7, double semanticThreshold = 0.6)
    {
        _connectionString = $"Data Source={dbPath};Pooling=True";
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var walCmd = conn.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000";
        walCmd.ExecuteNonQuery();

        Func<SqliteConnection> factory = () =>
        {
            var c = new SqliteConnection(_connectionString);
            c.Open();
            return c;
        };

        Temporal = new TemporalGraph(factory);
        Causal = new CausalGraph(factory, causalThreshold);
        Semantic = new SemanticGraph(factory, semanticThreshold);
        Entity = new EntityGraph(factory);
        IntentRouter = new IntentRouter();

        InitSchema(factory);
    }

    private void InitSchema(Func<SqliteConnection> factory)
    {
        using var conn = factory();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS nodes (
                id         TEXT PRIMARY KEY,
                session_id TEXT,
                content    TEXT NOT NULL,
                embedding  BLOB,
                created_at INTEGER NOT NULL,
                attributes TEXT,
                expires_at INTEGER
            );
            CREATE INDEX IF NOT EXISTS idx_nodes_expires ON nodes(expires_at);
            CREATE TABLE IF NOT EXISTS consolidation_queue (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                node_id    TEXT NOT NULL,
                status     TEXT NOT NULL DEFAULT 'pending',
                created_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_cq_status ON consolidation_queue(status);
            """;
        cmd.ExecuteNonQuery();

        // FTS5 virtual table for keyword search (replaces LIKE '%kw%')
        try
        {
            using var ftsCmd = conn.CreateCommand();
            ftsCmd.CommandText = """
                CREATE VIRTUAL TABLE IF NOT EXISTS nodes_fts USING fts5(
                    content, session_id,
                    tokenize='porter unicode61', content='nodes', content_rowid='rowid'
                );
                """;
            ftsCmd.ExecuteNonQuery();
            // Triggers to keep FTS5 in sync
            using var trigCmd = conn.CreateCommand();
            trigCmd.CommandText = """
                CREATE TRIGGER IF NOT EXISTS nodes_fts_insert AFTER INSERT ON nodes BEGIN
                    INSERT INTO nodes_fts(content, session_id) VALUES (new.content, new.session_id);
                END;
                CREATE TRIGGER IF NOT EXISTS nodes_fts_delete AFTER DELETE ON nodes BEGIN
                    INSERT INTO nodes_fts(nodes_fts, content, session_id) VALUES ('delete', old.content, old.session_id);
                END;
                CREATE TRIGGER IF NOT EXISTS nodes_fts_update AFTER UPDATE ON nodes BEGIN
                    INSERT INTO nodes_fts(nodes_fts, content, session_id) VALUES ('delete', old.content, old.session_id);
                    INSERT INTO nodes_fts(content, session_id) VALUES (new.content, new.session_id);
                END;
                """;
            trigCmd.ExecuteNonQuery();
        }
        catch { /* FTS5 may already exist / incompatible */ }

        // Migration: add expires_at column if missing
        try
        {
            using var migCmd = conn.CreateCommand();
            migCmd.CommandText = "ALTER TABLE nodes ADD COLUMN expires_at INTEGER";
            migCmd.ExecuteNonQuery();
            using var idxCmd = conn.CreateCommand();
            idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_nodes_expires ON nodes(expires_at)";
            idxCmd.ExecuteNonQuery();
        }
        catch { /* column/index already exists */ }

        ResetStaleConsolidationQueue();

        Temporal.InitSchema();
        Causal.InitSchema();
        Semantic.InitSchema();
        Entity.InitSchema();

        // Migration: add expires_at to edge tables
        MigrateEdgeTableExpiry("temporal_edges");
        MigrateEdgeTableExpiry("causal_edges");
        MigrateEdgeTableExpiry("semantic_edges");
        MigrateEdgeTableExpiry("entity_edges");
    }

    private void MigrateEdgeTableExpiry(string table)
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN expires_at INTEGER";
            cmd.ExecuteNonQuery();
        }
        catch { /* already exists */ }
    }

    private void ResetStaleConsolidationQueue()
    {
        try
        {
            var staleThreshold = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)CqResetTimeout.TotalSeconds;
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE consolidation_queue SET status='pending' WHERE status='processing' AND created_at<@ts";
            cmd.Parameters.AddWithValue("@ts", staleThreshold);
            cmd.ExecuteNonQuery();
        }
        catch { /* non-fatal */ }
    }

    public void StoreNode(string id, string sessionId, string content, byte[]? embedding = null, string? attributes = null)
    {
        lock (_gate)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var expiresAt = ts + NodeTtlSeconds;
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT OR REPLACE INTO nodes (id, session_id, content, embedding, created_at, attributes, expires_at)
                    VALUES (@id, @sid, @content, @emb, @ts, @attr, @exp)
                    """;
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@sid", sessionId);
                cmd.Parameters.AddWithValue("@content", content);
                cmd.Parameters.AddWithValue("@emb", embedding ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@ts", ts);
                cmd.Parameters.AddWithValue("@attr", attributes ?? "");
                cmd.Parameters.AddWithValue("@exp", expiresAt);
                cmd.ExecuteNonQuery();

                tx.Commit();

                // Post-commit sub-graph ops (non-fatal to main insert)
                try { Temporal.Append(id, (int)ts); } catch { }
                EnqueueConsolidation(id);
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

    /// <summary>
    /// Collision-safe node store: uses a monotonic counter suffix to prevent
    /// same-millisecond overwrites from dual-write (PalaceStore + MultiGraphStore).
    /// </summary>
    public void StoreNodeSafe(string prefix, string sessionId, string content, byte[]? embedding = null, string? attributes = null)
    {
        var seq = Interlocked.Increment(ref _nodeSeq);
        var id = $"{prefix}:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}:{seq}";
        StoreNode(id, sessionId, content, embedding, attributes);
    }

    public (string Id, string Content, long CreatedAt)? GetNode(string id)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, content, created_at FROM nodes WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        using var rdr = cmd.ExecuteReader();
        if (rdr.Read()) return (rdr.GetString(0), rdr.GetString(1), rdr.GetInt64(2));
        return null;
    }

    public List<string> SearchContent(string keyword, int limit = 20)
    {
        ResetStaleConsolidationQueue();
        // Prefer FTS5 (indexed), fall back to LIKE
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT n.id FROM nodes n
                JOIN nodes_fts fts ON fts.rowid = n.rowid
                WHERE nodes_fts MATCH @kw AND (n.expires_at IS NULL OR n.expires_at > @now)
                ORDER BY rank LIMIT @limit
                """;
            cmd.Parameters.AddWithValue("@kw", EscapeFts5(keyword));
            cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("@limit", limit);
            var results = new List<string>();
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read()) results.Add(rdr.GetString(0));
            if (results.Count > 0) return results;
        }
        catch { /* FTS5 may not exist yet */ }

        // Fallback: LIKE scan (works before FTS5 is populated)
        using var fbConn = new SqliteConnection(_connectionString);
        fbConn.Open();
        using var fallbackCmd = fbConn.CreateCommand();
        fallbackCmd.CommandText = """
            SELECT id FROM nodes
            WHERE content LIKE '%' || @kw || '%' AND (expires_at IS NULL OR expires_at > @now)
            ORDER BY created_at DESC LIMIT @limit
            """;
        fallbackCmd.Parameters.AddWithValue("@kw", keyword);
        fallbackCmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        fallbackCmd.Parameters.AddWithValue("@limit", limit);
        var fallbackResults = new List<string>();
        using var fbRdr = fallbackCmd.ExecuteReader();
        while (fbRdr.Read()) fallbackResults.Add(fbRdr.GetString(0));
        return fallbackResults;
    }

    private static string EscapeFts5(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "\"\"";
        return $"\"{raw.Replace("\"", "\"\"")}\"";
    }

    public void EnqueueConsolidation(string nodeId)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO consolidation_queue (node_id, status, created_at)
            VALUES (@node, 'pending', @ts)
            """;
        cmd.Parameters.AddWithValue("@node", nodeId);
        cmd.Parameters.AddWithValue("@ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    public List<string> DequeueConsolidationBatch(int maxBatch = 50)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE consolidation_queue SET status = 'processing'
            WHERE id IN (
                SELECT id FROM consolidation_queue
                WHERE status = 'pending'
                ORDER BY created_at ASC LIMIT @batch
            )
            RETURNING node_id
            """;
        cmd.Parameters.AddWithValue("@batch", maxBatch);
        var results = new List<string>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read()) results.Add(rdr.GetString(0));
        return results;
    }

    public void MarkConsolidated(string nodeId)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM consolidation_queue WHERE node_id = @node";
        cmd.Parameters.AddWithValue("@node", nodeId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Clean up expired nodes and their associated edges across all 4 edge tables.
    /// Call periodically (e.g., from MemoryConsolidationService).
    /// </summary>
    public int PurgeExpired()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var total = 0;

        lock (_gate)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            // 1. Collect expired node IDs
            var expiredIds = new List<string>();
            using var selCmd = conn.CreateCommand();
            selCmd.CommandText = "SELECT id FROM nodes WHERE expires_at IS NOT NULL AND expires_at < @now";
            selCmd.Parameters.AddWithValue("@now", now);
            using var rdr = selCmd.ExecuteReader();
            while (rdr.Read()) expiredIds.Add(rdr.GetString(0));

            if (expiredIds.Count == 0) return 0;

            // 2. Delete edges referencing expired nodes in all 4 edge tables
            var placeholders = string.Join(",", expiredIds.Select((_, i) => $"@p{i}"));
            var edgeTables = new[] { "temporal_edges", "causal_edges", "semantic_edges", "entity_edges" };
            foreach (var table in edgeTables)
            {
                try
                {
                    using var edgeCmd = conn.CreateCommand();
                    edgeCmd.CommandText = $"DELETE FROM {table} WHERE from_id IN ({placeholders}) OR to_id IN ({placeholders})";
                    for (int i = 0; i < expiredIds.Count; i++)
                        edgeCmd.Parameters.AddWithValue($"@p{i}", expiredIds[i]);
                    total += edgeCmd.ExecuteNonQuery();
                }
                catch { /* table may not exist yet */ }
            }

            // 3. Delete expired nodes
            using var delCmd = conn.CreateCommand();
            delCmd.CommandText = "DELETE FROM nodes WHERE id IN (" + placeholders + ")";
            for (int i = 0; i < expiredIds.Count; i++)
                delCmd.Parameters.AddWithValue($"@p{i}", expiredIds[i]);
            total += delCmd.ExecuteNonQuery();
        }

        return total;
    }

    public void Dispose()
    {
        ResetStaleConsolidationQueue();
    }
}
