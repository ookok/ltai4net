using Microsoft.Data.Sqlite;

namespace LTAI.Agent.Memory;

public sealed class MultiGraphStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly string _dbPath;

    public TemporalGraph Temporal { get; }
    public CausalGraph Causal { get; }
    public SemanticGraph Semantic { get; }
    public EntityGraph Entity { get; }
    public IntentRouter IntentRouter { get; }

    public MultiGraphStore(string dbPath, double causalThreshold = 0.7, double semanticThreshold = 0.6)
    {
        _dbPath = dbPath;
        _db = new SqliteConnection($"Data Source={dbPath};Pooling=True");
        _db.Open();

        using var walCmd = _db.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000";
        walCmd.ExecuteNonQuery();

        Temporal = new TemporalGraph(_db);
        Causal = new CausalGraph(_db, causalThreshold);
        Semantic = new SemanticGraph(_db, semanticThreshold);
        Entity = new EntityGraph(_db);
        IntentRouter = new IntentRouter();

        InitSchema();
    }

    private void InitSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS nodes (
                id         TEXT PRIMARY KEY,
                session_id TEXT,
                content    TEXT NOT NULL,
                embedding  BLOB,
                created_at INTEGER NOT NULL,
                attributes TEXT
            );
            CREATE TABLE IF NOT EXISTS consolidation_queue (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                node_id    TEXT NOT NULL,
                status     TEXT NOT NULL DEFAULT 'pending',
                created_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_cq_status ON consolidation_queue(status);
            """;
        cmd.ExecuteNonQuery();

        Temporal.InitSchema();
        Causal.InitSchema();
        Semantic.InitSchema();
        Entity.InitSchema();
    }

    public void StoreNode(string id, string sessionId, string content, byte[]? embedding = null, string? attributes = null)
    {
        using var tx = _db.BeginTransaction();
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO nodes (id, session_id, content, embedding, created_at, attributes)
                VALUES (@id, @sid, @content, @emb, @ts, @attr)
                """;
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@sid", sessionId);
            cmd.Parameters.AddWithValue("@content", content);
            cmd.Parameters.AddWithValue("@emb", embedding ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("@attr", attributes ?? "");
            cmd.ExecuteNonQuery();

            // Fast Path: temporal edge + consolidation queue
            Temporal.Append(id, (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            EnqueueConsolidation(id);

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public (string Id, string Content, long CreatedAt)? GetNode(string id)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id, content, created_at FROM nodes WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        using var rdr = cmd.ExecuteReader();
        if (rdr.Read()) return (rdr.GetString(0), rdr.GetString(1), rdr.GetInt64(2));
        return null;
    }

    public List<string> SearchContent(string keyword, int limit = 20)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT id FROM nodes
            WHERE content LIKE '%' || @kw || '%'
            ORDER BY created_at DESC LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@kw", keyword);
        cmd.Parameters.AddWithValue("@limit", limit);
        var results = new List<string>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read()) results.Add(rdr.GetString(0));
        return results;
    }

    public void EnqueueConsolidation(string nodeId)
    {
        using var cmd = _db.CreateCommand();
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
        using var cmd = _db.CreateCommand();
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
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM consolidation_queue WHERE node_id = @node";
        cmd.Parameters.AddWithValue("@node", nodeId);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _db?.Close();
        _db?.Dispose();
    }
}
