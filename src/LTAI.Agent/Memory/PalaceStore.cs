// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  PalaceStore — structured long-term memory (shares kg.db)
//  OPTIMIZED: uses CreateShared() to share KgStore's kg.db.
// ═══════════════════════════════════════════════════════════════

using System.Text.Json;
using LTAI.AI;
using LTAI.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

[ToolDomain("memory")]
public sealed class PalaceStore
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS palace (
            wing TEXT NOT NULL, room TEXT NOT NULL, drawer_id TEXT NOT NULL,
            role TEXT NOT NULL DEFAULT 'assistant', content TEXT NOT NULL, embedding BLOB,
            created_at INTEGER NOT NULL, expires_at INTEGER, importance REAL NOT NULL DEFAULT 0.5,
            agent_id TEXT NOT NULL DEFAULT 'default', metadata TEXT,
            access_count INTEGER NOT NULL DEFAULT 0, last_accessed_at INTEGER,
            PRIMARY KEY (wing, room, drawer_id)
        );
        CREATE INDEX IF NOT EXISTS idx_palace_wing_room ON palace(wing, room);
        CREATE INDEX IF NOT EXISTS idx_palace_agent ON palace(agent_id);
        CREATE INDEX IF NOT EXISTS idx_palace_created ON palace(created_at DESC);
        CREATE INDEX IF NOT EXISTS idx_palace_expires ON palace(expires_at);
        CREATE INDEX IF NOT EXISTS idx_palace_access ON palace(access_count DESC);
        """;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false,
    };

    private readonly EmbeddingClient _embedder;
    private readonly string _connectionString;
    private readonly ILogger<PalaceStore>? _logger;
    private bool _schemaReady;
    private readonly object _gate = new();

    public PalaceStore(EmbeddingClient embedder, string dbPath, ILogger<PalaceStore>? logger = null)
    {
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _logger = logger;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared,
        }.ToString();
        EnsureSchema();
    }

    public static PalaceStore CreateShared(EmbeddingClient embedder, string kgDbPath, ILogger<PalaceStore>? logger = null)
        => new(embedder, kgDbPath, logger);

    public record Drawer(string Wing, string Room, string DrawerId, string Role, string Content,
        float[]? Embedding, long CreatedAt, long? ExpiresAt, double Importance, string AgentId, string? Metadata,
        int AccessCount = 0, long? LastAccessedAt = null);

    public const long DefaultTtlMs = 30L * 24 * 60 * 60 * 1000;

    // ═══════════════════════════════════════════
    //  Core API
    // ═══════════════════════════════════════════

    public async Task<string> StoreAsync(string wing, string room, string content,
        string role = "assistant", double importance = 0.5, string? agentId = null,
        Dictionary<string, object>? metadata = null, long? ttlMs = null)
    {
        EnsureSchema();
        var drawerId = Guid.NewGuid().ToString("n");
        var vec = await _embedder.GenerateAsync(content, CancellationToken.None).ConfigureAwait(false);

        await foreach (var hit in SemanticSearchAsync(vec, topK: 1, wing: wing).ConfigureAwait(false))
            if (hit.Score >= 0.92) return hit.Drawer.DrawerId;

        var expiresAt = ttlMs is > 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ttlMs.Value
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + DefaultTtlMs;

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO palace (wing,room,drawer_id,role,content,embedding,created_at,expires_at,importance,agent_id,metadata) VALUES ($w,$r,$id,$role,$c,$emb,$now,$exp,$imp,$agent,$meta)";
        cmd.Parameters.AddWithValue("$w", wing); cmd.Parameters.AddWithValue("$r", room);
        cmd.Parameters.AddWithValue("$id", drawerId); cmd.Parameters.AddWithValue("$role", role);
        cmd.Parameters.AddWithValue("$c", content); cmd.Parameters.AddWithValue("$emb", vec.SelectMany(BitConverter.GetBytes).ToArray());
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); cmd.Parameters.AddWithValue("$exp", expiresAt);
        cmd.Parameters.AddWithValue("$imp", importance); cmd.Parameters.AddWithValue("$agent", agentId ?? "default");
        cmd.Parameters.AddWithValue("$meta", metadata != null ? JsonSerializer.Serialize(metadata, JsonOpts) : DBNull.Value);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        return drawerId;
    }

    public bool TouchDrawer(string wing, string room, string drawerId, double importance)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE palace SET importance=MAX(importance,$imp),expires_at=$exp WHERE wing=$w AND room=$r AND drawer_id=$id";
        cmd.Parameters.AddWithValue("$imp", importance); cmd.Parameters.AddWithValue("$exp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + DefaultTtlMs);
        cmd.Parameters.AddWithValue("$w", wing); cmd.Parameters.AddWithValue("$r", room); cmd.Parameters.AddWithValue("$id", drawerId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public Drawer? GetDrawer(string wing, string room, string drawerId)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM palace WHERE wing=$w AND room=$r AND drawer_id=$id AND (expires_at IS NULL OR expires_at>$now)";
        cmd.Parameters.AddWithValue("$w", wing); cmd.Parameters.AddWithValue("$r", room); cmd.Parameters.AddWithValue("$id", drawerId);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var rdr = cmd.ExecuteReader();
        return rdr.Read() ? ReadDrawer(rdr) : null;
    }

    public async IAsyncEnumerable<(Drawer Drawer, double Score)> SemanticSearchAsync(
        float[] queryVec, int topK = 5, string? wing = null, string? room = null)
    {
        EnsureSchema();
        var scored = new List<(Drawer, double)>();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        var sql = "SELECT * FROM palace WHERE embedding IS NOT NULL AND (expires_at IS NULL OR expires_at>$now)";
        if (wing != null) sql += " AND wing=$w";
        if (room != null) sql += " AND room=$r";
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (wing != null) cmd.Parameters.AddWithValue("$w", wing);
        if (room != null) cmd.Parameters.AddWithValue("$r", room);
        using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (rdr.Read())
        {
            var drawer = ReadDrawer(rdr);
            if (drawer.Embedding is { Length: > 0 })
                scored.Add((drawer, CosineSimilarity(queryVec, drawer.Embedding)));
        }
        foreach (var hit in scored.OrderByDescending(x => x.Item2).Take(topK))
        {
            // Record access for retrieval feedback
            RecordAccess(hit.Item1.DrawerId);
            yield return hit;
        }
    }

    public IReadOnlyList<Drawer> GetRecentDrawers(string wing, string room, int limit = 10)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM palace WHERE wing=$w AND room=$r AND (expires_at IS NULL OR expires_at>$now) ORDER BY created_at DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$w", wing); cmd.Parameters.AddWithValue("$r", room);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); cmd.Parameters.AddWithValue("$limit", limit);
        using var rdr = cmd.ExecuteReader();
        var results = new List<Drawer>();
        while (rdr.Read()) results.Add(ReadDrawer(rdr));
        return results;
    }

    public IReadOnlyList<Drawer> GetImportantDrawers(string wing, double threshold = 0.8, int limit = 20)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM palace WHERE wing=$w AND importance>=$th AND (expires_at IS NULL OR expires_at>$now) ORDER BY importance DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$w", wing); cmd.Parameters.AddWithValue("$th", threshold);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); cmd.Parameters.AddWithValue("$limit", limit);
        using var rdr = cmd.ExecuteReader();
        var results = new List<Drawer>();
        while (rdr.Read()) results.Add(ReadDrawer(rdr));
        return results;
    }

    public int PurgeExpired()
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM palace WHERE expires_at IS NOT NULL AND expires_at<$now";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var count = cmd.ExecuteNonQuery();
        if (count > 0) _logger?.LogInformation("Purged {Count} expired entries", count);
        return count;
    }

    public long Count()
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM palace WHERE expires_at IS NULL OR expires_at>$now";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return (long)cmd.ExecuteScalar()!;
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text)
        => await _embedder.GenerateAsync(text, CancellationToken.None).ConfigureAwait(false);

    // ═══════════════════════════════════════════
    //  Extended API (consumed by MemoryConsolidationService, L1-6 providers, MemoryTools)
    // ═══════════════════════════════════════════

    public int CleanupExpired() => PurgeExpired();

    public int DecayAll(double factor = 0.95, double minImportance = 0.05)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE palace SET importance=MAX(importance*$f,$m) WHERE importance>$m";
        cmd.Parameters.AddWithValue("$f", factor); cmd.Parameters.AddWithValue("$m", minImportance);
        return cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<string> ListWings()
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT wing FROM palace WHERE (expires_at IS NULL OR expires_at>$now) ORDER BY wing";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var rdr = cmd.ExecuteReader();
        var list = new List<string>();
        while (rdr.Read()) list.Add(rdr.GetString(0));
        return list;
    }

    public IReadOnlyList<(string Wing, string Room)> ListRooms(string? wing = null)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        if (wing != null) { cmd.CommandText = "SELECT DISTINCT wing,room FROM palace WHERE wing=$w AND (expires_at IS NULL OR expires_at>$now) ORDER BY room"; cmd.Parameters.AddWithValue("$w", wing); }
        else cmd.CommandText = "SELECT DISTINCT wing,room FROM palace WHERE (expires_at IS NULL OR expires_at>$now) ORDER BY wing,room";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var rdr = cmd.ExecuteReader();
        var list = new List<(string, string)>();
        while (rdr.Read()) list.Add((rdr.GetString(0), rdr.GetString(1)));
        return list;
    }

    public int ConsolidateRoom(string wing, (string Wing, string Room) room) => ConsolidateRoom(wing, room.Room);

    public int ConsolidateRoom(string wing, string room)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var findCmd = conn.CreateCommand();
        findCmd.CommandText = "SELECT drawer_id FROM palace WHERE wing=$w AND room=$r AND (expires_at IS NULL OR expires_at>$now) ORDER BY importance DESC LIMIT 1";
        findCmd.Parameters.AddWithValue("$w", wing); findCmd.Parameters.AddWithValue("$r", room);
        findCmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var best = findCmd.ExecuteScalar() as string;
        if (best == null) return 0;
        using var delCmd = conn.CreateCommand();
        delCmd.CommandText = "DELETE FROM palace WHERE wing=$w AND room=$r AND drawer_id!=$best AND (expires_at IS NULL OR expires_at>$now)";
        delCmd.Parameters.AddWithValue("$w", wing); delCmd.Parameters.AddWithValue("$r", room); delCmd.Parameters.AddWithValue("$best", best);
        delCmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var deleted = delCmd.ExecuteNonQuery();
        if (deleted > 0) _logger?.LogDebug("Consolidated {Count} in {W}/{R}", deleted, wing, room);
        return deleted;
    }

    public IReadOnlyList<Drawer> GetEssentialMoments(int maxCount = 10, string? agentId = null)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        var sql = "SELECT * FROM palace WHERE importance>=0.3 AND (expires_at IS NULL OR expires_at>$now)";
        if (agentId != null) sql += " AND agent_id=$agent";
        sql += " ORDER BY importance DESC,created_at DESC LIMIT $limit";
        cmd.CommandText = sql; cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$limit", maxCount);
        if (agentId != null) cmd.Parameters.AddWithValue("$agent", agentId);
        using var rdr = cmd.ExecuteReader();
        var list = new List<Drawer>();
        while (rdr.Read()) list.Add(ReadDrawer(rdr));
        return list;
    }

    public IReadOnlyList<Drawer> SearchByWing(string wing, int maxCount = 10)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM palace WHERE wing=$w AND (expires_at IS NULL OR expires_at>$now) ORDER BY importance DESC,created_at DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$w", wing); cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); cmd.Parameters.AddWithValue("$limit", maxCount);
        using var rdr = cmd.ExecuteReader();
        var list = new List<Drawer>();
        while (rdr.Read()) list.Add(ReadDrawer(rdr));
        return list;
    }

    public IReadOnlyList<Drawer> SearchByRoom(string room, string? agentId = null, int maxCount = 10)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        var sql = "SELECT * FROM palace WHERE room=$r AND (expires_at IS NULL OR expires_at>$now)";
        if (agentId != null) sql += " AND agent_id=$agent";
        sql += " ORDER BY created_at DESC LIMIT $limit";
        cmd.CommandText = sql; cmd.Parameters.AddWithValue("$r", room); cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$limit", maxCount);
        if (agentId != null) cmd.Parameters.AddWithValue("$agent", agentId);
        using var rdr = cmd.ExecuteReader();
        var list = new List<Drawer>();
        while (rdr.Read()) list.Add(ReadDrawer(rdr));
        return list;
    }

    public IReadOnlyList<Drawer> SearchByRoomExact(string wing, string? agentId = null)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        var sql = "SELECT * FROM palace WHERE wing=$w AND (expires_at IS NULL OR expires_at>$now)";
        if (agentId != null) sql += " AND agent_id=$agent";
        sql += " ORDER BY created_at DESC";
        cmd.CommandText = sql; cmd.Parameters.AddWithValue("$w", wing); cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (agentId != null) cmd.Parameters.AddWithValue("$agent", agentId);
        using var rdr = cmd.ExecuteReader();
        var list = new List<Drawer>();
        while (rdr.Read()) list.Add(ReadDrawer(rdr));
        return list;
    }

    /// <summary>Get all non-expired drawers (for MemoryBrowser UI).</summary>
    public IReadOnlyList<Drawer> GetAllDrawers()
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM palace WHERE (expires_at IS NULL OR expires_at>$now) ORDER BY created_at DESC LIMIT 200";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var rdr = cmd.ExecuteReader();
        var list = new List<Drawer>();
        while (rdr.Read()) list.Add(ReadDrawer(rdr));
        return list;
    }

    /// <summary>Delete a single drawer by its ID (wing+room+drawerId overload for MemoryBrowser).</summary>
    public bool DeleteDrawer(string wing, string room, string drawerId)
        => DeleteDrawer(drawerId);

    /// <summary>Delete a single drawer by its ID.</summary>
    public bool DeleteDrawer(string drawerId)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM palace WHERE drawer_id=$id";
        cmd.Parameters.AddWithValue("$id", drawerId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public int DeleteWingRoom(string wing, string room)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM palace WHERE wing=$w AND room=$r";
        cmd.Parameters.AddWithValue("$w", wing); cmd.Parameters.AddWithValue("$r", room);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Record access to a drawer: increment counter and update timestamp.</summary>
    public void RecordAccess(string drawerId)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE palace SET access_count=access_count+1, last_accessed_at=$now WHERE drawer_id=$id";
        cmd.Parameters.AddWithValue("$id", drawerId);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    /// <summary>Get most frequently accessed memories across all wings.</summary>
    public IReadOnlyList<Drawer> GetPopularDrawers(int limit = 10)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM palace WHERE access_count>0 AND (expires_at IS NULL OR expires_at>$now) ORDER BY access_count DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$limit", limit);
        using var rdr = cmd.ExecuteReader();
        var list = new List<Drawer>();
        while (rdr.Read()) list.Add(ReadDrawer(rdr));
        return list;
    }

    // ═══════════════════════════════════════════
    //  Private
    // ═══════════════════════════════════════════

    private void EnsureSchema()
    {
        if (_schemaReady) return;
        lock (_gate)
        {
            if (_schemaReady) return;
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = Schema;
            cmd.ExecuteNonQuery();
            // Migration: add access tracking columns if missing
            try
            {
                using var migCmd = conn.CreateCommand();
                migCmd.CommandText = "ALTER TABLE palace ADD COLUMN access_count INTEGER NOT NULL DEFAULT 0";
                migCmd.ExecuteNonQuery();
            }
            catch { /* column already exists */ }
            try
            {
                using var migCmd = conn.CreateCommand();
                migCmd.CommandText = "ALTER TABLE palace ADD COLUMN last_accessed_at INTEGER";
                migCmd.ExecuteNonQuery();
            }
            catch { /* column already exists */ }
            _schemaReady = true;
        }
    }

    private static double CosineSimilarity(float[] a, float[] b) => VectorMath.CosineSimilarity(a.AsSpan(), b.AsSpan());

    private static Drawer ReadDrawer(SqliteDataReader r)
    {
        var baseDrawer = new Drawer(
            r.GetString(0), r.GetString(1), r.GetString(2),
            r.IsDBNull(3) ? "assistant" : r.GetString(3), r.GetString(4),
            r.IsDBNull(5) ? null : DeserializeEmb(r, 5), r.GetInt64(6),
            r.IsDBNull(7) ? null : r.GetInt64(7), r.IsDBNull(8) ? 0.5 : r.GetDouble(8),
            r.IsDBNull(9) ? "default" : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10));
        // Read access tracking columns (if they exist in query result)
        try { return baseDrawer with { AccessCount = r.IsDBNull(11) ? 0 : r.GetInt32(11), LastAccessedAt = r.IsDBNull(12) ? null : r.GetInt64(12) }; }
        catch { return baseDrawer; }
    }

    private static float[]? DeserializeEmb(SqliteDataReader r, int c)
    {
        if (r.IsDBNull(c)) return null;
        var bytes = (byte[])r[c]; var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }
}
