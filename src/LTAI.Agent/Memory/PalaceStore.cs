// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  PalaceStore — structured long-term memory (shares kg.db)
//  TurboQuant 4-bit vectors (192B vs 1536B raw) + FTS5 BM25 + HNSW.
// ═══════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LTAI.AI;
using LTAI.Agent.Vector;
using LTAI.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using TurboQuant.Core.Packing;

namespace LTAI.Agent.Memory;

[ToolDomain("memory")]
public sealed partial class PalaceStore : IDisposable
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS palace (
            wing TEXT NOT NULL, room TEXT NOT NULL, drawer_id TEXT NOT NULL,
            role TEXT NOT NULL DEFAULT 'assistant', content TEXT NOT NULL, embedding BLOB,
            created_at INTEGER NOT NULL, expires_at INTEGER, importance REAL NOT NULL DEFAULT 0.5,
            agent_id TEXT NOT NULL DEFAULT 'default', metadata TEXT,
            access_count INTEGER NOT NULL DEFAULT 0, last_accessed_at INTEGER,
            principal TEXT DEFAULT NULL, scope TEXT DEFAULT 'shared',
            PRIMARY KEY (wing, room, drawer_id)
        );
        CREATE INDEX IF NOT EXISTS idx_palace_wing_room ON palace(wing, room);
        CREATE INDEX IF NOT EXISTS idx_palace_agent ON palace(agent_id);
        CREATE INDEX IF NOT EXISTS idx_palace_created ON palace(created_at DESC);
        CREATE INDEX IF NOT EXISTS idx_palace_expires ON palace(expires_at);
        CREATE INDEX IF NOT EXISTS idx_palace_access ON palace(access_count DESC);
        CREATE INDEX IF NOT EXISTS idx_palace_importance ON palace(importance DESC, created_at DESC);
        CREATE INDEX IF NOT EXISTS idx_palace_wing_import ON palace(wing, importance DESC);
        CREATE INDEX IF NOT EXISTS idx_palace_room_created ON palace(room, created_at DESC);
        CREATE INDEX IF NOT EXISTS idx_palace_principal ON palace(principal);
        CREATE INDEX IF NOT EXISTS idx_palace_scope ON palace(scope);
        """;

    private const string FtsSchema = """
        CREATE VIRTUAL TABLE IF NOT EXISTS palace_fts USING fts5(
            content, wing, room, drawer_id,
            tokenize='porter unicode61', content='palace', content_rowid='rowid'
        );
        CREATE TRIGGER IF NOT EXISTS palace_fts_insert AFTER INSERT ON palace BEGIN
            INSERT INTO palace_fts(content, wing, room, drawer_id)
            VALUES (new.content, new.wing, new.room, new.drawer_id);
        END;
        CREATE TRIGGER IF NOT EXISTS palace_fts_delete AFTER DELETE ON palace BEGIN
            INSERT INTO palace_fts(palace_fts, content, wing, room, drawer_id)
            VALUES ('delete', old.content, old.wing, old.room, old.drawer_id);
        END;
        CREATE TRIGGER IF NOT EXISTS palace_fts_update AFTER UPDATE ON palace BEGIN
            INSERT INTO palace_fts(palace_fts, content, wing, room, drawer_id)
            VALUES ('delete', old.content, old.wing, old.room, old.drawer_id);
            INSERT INTO palace_fts(content, wing, room, drawer_id)
            VALUES (new.content, new.wing, new.room, new.drawer_id);
        END;
        """;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false,
    };

    private readonly EmbeddingClient _embedder;
    private readonly string _connectionString;
    internal string ConnectionString => _connectionString;
    private readonly string _dbPath;
    private readonly ILogger<PalaceStore>? _logger;
    private bool _schemaReady;
    private readonly object _gate = new();

    // Search result cache: 30s TTL, keyed by query hash + wing + room
    private readonly ConcurrentDictionary<string, (DateTime Expiry, IReadOnlyList<(Drawer Drawer, double Score)> Results)> _searchCache = new();
    private static readonly TimeSpan SearchCacheTtl = TimeSpan.FromSeconds(30);

    public PalaceStore(EmbeddingClient embedder, string dbPath, ILogger<PalaceStore>? logger = null)
    {
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _logger = logger;
        _dbPath = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
        EnsureSchema();
    }

    public static PalaceStore CreateShared(EmbeddingClient embedder, string kgDbPath, ILogger<PalaceStore>? logger = null)
        => new(embedder, kgDbPath, logger);

    public record Drawer(string Wing, string Room, string DrawerId, string Role, string Content,
        float[]? Embedding, long CreatedAt, long? ExpiresAt, double Importance, string AgentId, string? Metadata,
        int AccessCount = 0, long? LastAccessedAt = null,
        string? Principal = null, string? Scope = "shared");

    public const long DefaultTtlMs = 30L * 24 * 60 * 60 * 1000;
    public const int DefaultMaxEntries = 10000;

    private int _maxEntries = DefaultMaxEntries;
    public int MaxEntries
    {
        get => _maxEntries;
        set => _maxEntries = Math.Max(100, value);
    }

    // ═══════════════════════════════════════════
    //  Core API
    // ═══════════════════════════════════════════

    public async Task<string> StoreAsync(string wing, string room, string content,
        string role = "assistant", double importance = 0.5, string? agentId = null,
        Dictionary<string, object>? metadata = null, long? ttlMs = null,
        string? principal = null, string? scope = "shared")
    {
        if (string.IsNullOrEmpty(wing)) throw new ArgumentException("wing must not be null/empty");
        if (string.IsNullOrEmpty(room)) throw new ArgumentException("room must not be null/empty");
        EnsureSchema();
        _ = WarmupHnswAsync();
        var drawerId = Guid.NewGuid().ToString("n");
        var vec = await _embedder.GenerateAsync(content, CancellationToken.None).ConfigureAwait(false);

        await foreach (var hit in SemanticSearchAsync(vec, topK: 1, wing: wing).ConfigureAwait(false))
            if (hit.Score >= 0.92) return hit.Drawer.DrawerId;

        long? expiresAt = null;
        if (ttlMs is > 0)
            expiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ttlMs.Value;
        else if (ttlMs == null)
            expiresAt = null;
        else
            expiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + DefaultTtlMs;

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO palace (wing,room,drawer_id,role,content,embedding,created_at,expires_at,importance,agent_id,metadata,principal,scope) VALUES ($w,$r,$id,$role,$c,$emb,$now,$exp,$imp,$agent,$meta,$principal,$scope)";
        cmd.Parameters.AddWithValue("$w", wing); cmd.Parameters.AddWithValue("$r", room);
        cmd.Parameters.AddWithValue("$id", drawerId); cmd.Parameters.AddWithValue("$role", role);
        cmd.Parameters.AddWithValue("$c", content);

        if (vec.Length == VectorQuantizer.Dim)
            cmd.Parameters.AddWithValue("$emb", VectorQuantizer.QuantizeToBytes(vec));
        else
        {
            _logger?.LogWarning("PalaceStore: embedding dim mismatch: got {Actual}, expected {Expected}. Storing without embedding.", vec.Length, VectorQuantizer.Dim);
            cmd.Parameters.AddWithValue("$emb", DBNull.Value);
        }
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); cmd.Parameters.AddWithValue("$exp", (object?)expiresAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$imp", importance); cmd.Parameters.AddWithValue("$agent", agentId ?? "default");
        cmd.Parameters.AddWithValue("$meta", metadata != null ? JsonSerializer.Serialize(metadata, JsonOpts) : DBNull.Value);
        cmd.Parameters.AddWithValue("$principal", (object?)principal ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$scope", scope ?? "shared");
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

        // Insert into HNSW index under lock
        if (vec.Length == VectorQuantizer.Dim)
        {
            await _hnswLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var idx = _hnsw.Insert(vec);
                _hnswMap[idx] = drawerId;
                _hnswRev[drawerId] = idx;
            }
            finally { _hnswLock.Release(); }
        }

        var currentCount = await CountAsync().ConfigureAwait(false);
        if (currentCount > _maxEntries * 1.1)
            _ = Task.Run(async () =>
            {
                try { await TrimAsync(_maxEntries).ConfigureAwait(false); }
                catch (Exception ex) { _logger?.LogWarning(ex, "PalaceStore background TrimAsync failed"); }
            });

        // ── Entity Surfacing (MeMo-inspired) ──
        try
        {
            var entityPairs = SurfaceEntitiesFromContent(content, wing, room);
            foreach (var (q, a) in entityPairs)
            {
                var companionId = Guid.NewGuid().ToString("n");
                var companionContent = $"Q: {q}\nA: {a}";
                using var compCmd = conn.CreateCommand();
                compCmd.CommandText = "INSERT OR IGNORE INTO palace (wing,room,drawer_id,role,content,created_at,expires_at,importance,agent_id) VALUES ($w,$r,$id,'system',$c,$now,$exp,0.3,$agent)";
                compCmd.Parameters.AddWithValue("$w", wing);
                compCmd.Parameters.AddWithValue("$r", room + ".entity");
                compCmd.Parameters.AddWithValue("$id", companionId);
                compCmd.Parameters.AddWithValue("$c", companionContent);
                compCmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                compCmd.Parameters.AddWithValue("$exp", expiresAt.HasValue ? (object)expiresAt.Value : DBNull.Value);
                compCmd.Parameters.AddWithValue("$agent", agentId ?? "default");
                await compCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }
        catch { /* entity surfacing is best-effort */ }

        return drawerId;
    }

    /// <summary>Trim to target count by evicting oldest + least-important entries.</summary>
    public async Task TrimAsync(int targetCount)
    {
        EnsureSchema();
        var current = await CountAsync().ConfigureAwait(false);
        if (current <= targetCount) return;
        var toEvict = (int)(current - targetCount);
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM palace WHERE drawer_id IN (SELECT drawer_id FROM palace WHERE (expires_at IS NULL OR expires_at>$now) ORDER BY importance ASC, created_at ASC LIMIT $limit)";
        cmd.Parameters.AddWithValue("$limit", toEvict);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<bool> TouchDrawerAsync(string wing, string room, string drawerId, double importance)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE palace SET importance=$imp, last_accessed_at=$now WHERE wing=$w AND room=$r AND drawer_id=$id";
        cmd.Parameters.AddWithValue("$w", wing); cmd.Parameters.AddWithValue("$r", room); cmd.Parameters.AddWithValue("$id", drawerId);
        cmd.Parameters.AddWithValue("$imp", importance); cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false) > 0;
    }

    public bool TouchDrawer(string wing, string room, string drawerId, double importance)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE palace SET importance=$imp, last_accessed_at=$now WHERE wing=$w AND room=$r AND drawer_id=$id";
        cmd.Parameters.AddWithValue("$w", wing); cmd.Parameters.AddWithValue("$r", room); cmd.Parameters.AddWithValue("$id", drawerId);
        cmd.Parameters.AddWithValue("$imp", importance); cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return cmd.ExecuteNonQuery() > 0;
    }

    public Drawer? GetDrawer(string wing, string room, string drawerId)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM palace WHERE wing=$w AND room=$r AND drawer_id=$id";
        cmd.Parameters.AddWithValue("$w", wing); cmd.Parameters.AddWithValue("$r", room); cmd.Parameters.AddWithValue("$id", drawerId);
        using var rdr = cmd.ExecuteReader();
        return rdr.Read() ? ReadDrawer(rdr) : null;
    }

    public async Task<Drawer?> GetDrawerAsync(string wing, string room, string drawerId)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM palace WHERE wing=$w AND room=$r AND drawer_id=$id";
        cmd.Parameters.AddWithValue("$w", wing); cmd.Parameters.AddWithValue("$r", room); cmd.Parameters.AddWithValue("$id", drawerId);
        using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        return await rdr.ReadAsync().ConfigureAwait(false) ? ReadDrawer(rdr) : null;
    }

    public Drawer? GetDrawerById(string drawerId)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM palace WHERE drawer_id=$id";
        cmd.Parameters.AddWithValue("$id", drawerId);
        using var rdr = cmd.ExecuteReader();
        return rdr.Read() ? ReadDrawer(rdr) : null;
    }

    public async Task<Drawer?> GetDrawerByIdAsync(string drawerId)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM palace WHERE drawer_id=$id";
        cmd.Parameters.AddWithValue("$id", drawerId);
        using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        return await rdr.ReadAsync().ConfigureAwait(false) ? ReadDrawer(rdr) : null;
    }

    public int PurgeExpired()
    {
        EnsureSchema();
        var expiredIds = new List<string>();
        using (var selConn = new SqliteConnection(_connectionString))
        {
            selConn.Open();
            using var selCmd = selConn.CreateCommand();
            selCmd.CommandText = "SELECT drawer_id FROM palace WHERE expires_at IS NOT NULL AND expires_at<$now";
            selCmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            using var rdr = selCmd.ExecuteReader();
            while (rdr.Read()) expiredIds.Add(rdr.GetString(0));
        }

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM palace WHERE expires_at IS NOT NULL AND expires_at<$now";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var count = cmd.ExecuteNonQuery();

        foreach (var id in expiredIds)
            _removed.TryAdd(id, 1);

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

    public async Task<long> CountAsync()
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM palace WHERE expires_at IS NULL OR expires_at>$now";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return (long)(await cmd.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text)
        => await _embedder.GenerateAsync(text, CancellationToken.None).ConfigureAwait(false);

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

    public async Task<int> DecayAllAsync(double factor = 0.95, double minImportance = 0.05)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE palace SET importance=MAX(importance*$f,$m) WHERE importance>$m";
        cmd.Parameters.AddWithValue("$f", factor); cmd.Parameters.AddWithValue("$m", minImportance);
        return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
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

    public async Task<IReadOnlyList<Drawer>> GetRecentDrawersAsync(string wing, string room, int limit = 10)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM palace WHERE wing=$w AND room=$r AND (expires_at IS NULL OR expires_at>$now) ORDER BY created_at DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$w", wing); cmd.Parameters.AddWithValue("$r", room);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); cmd.Parameters.AddWithValue("$limit", limit);
        using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        var results = new List<Drawer>();
        while (await rdr.ReadAsync().ConfigureAwait(false)) results.Add(ReadDrawer(rdr));
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

    public async Task<IReadOnlyList<Drawer>> GetImportantDrawersAsync(string wing, double threshold = 0.8, int limit = 20)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM palace WHERE wing=$w AND importance>=$th AND (expires_at IS NULL OR expires_at>$now) ORDER BY importance DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$w", wing); cmd.Parameters.AddWithValue("$th", threshold);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); cmd.Parameters.AddWithValue("$limit", limit);
        using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        var results = new List<Drawer>();
        while (await rdr.ReadAsync().ConfigureAwait(false)) results.Add(ReadDrawer(rdr));
        return results;
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

    public async Task<IReadOnlyList<string>> ListWingsAsync()
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT wing FROM palace WHERE (expires_at IS NULL OR expires_at>$now) ORDER BY wing";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        var list = new List<string>();
        while (await rdr.ReadAsync().ConfigureAwait(false)) list.Add(rdr.GetString(0));
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

    public async Task<IReadOnlyList<(string Wing, string Room)>> ListRoomsAsync(string? wing = null)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        if (wing != null) { cmd.CommandText = "SELECT DISTINCT wing,room FROM palace WHERE wing=$w AND (expires_at IS NULL OR expires_at>$now) ORDER BY room"; cmd.Parameters.AddWithValue("$w", wing); }
        else cmd.CommandText = "SELECT DISTINCT wing,room FROM palace WHERE (expires_at IS NULL OR expires_at>$now) ORDER BY wing,room";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        var list = new List<(string, string)>();
        while (await rdr.ReadAsync().ConfigureAwait(false)) list.Add((rdr.GetString(0), rdr.GetString(1)));
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

    public async Task<int> ConsolidateRoomAsync(string wing, string room)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var tx = conn.BeginTransaction();
        try
        {
            using var findCmd = conn.CreateCommand();
            findCmd.Transaction = tx;
            findCmd.CommandText = "SELECT drawer_id FROM palace WHERE wing=$w AND room=$r AND (expires_at IS NULL OR expires_at>$now) ORDER BY importance DESC LIMIT 1";
            findCmd.Parameters.AddWithValue("$w", wing); findCmd.Parameters.AddWithValue("$r", room);
            findCmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var best = (await findCmd.ExecuteScalarAsync().ConfigureAwait(false)) as string;
            if (best == null) { tx.Rollback(); return 0; }

            using var delCmd = conn.CreateCommand();
            delCmd.Transaction = tx;
            delCmd.CommandText = "DELETE FROM palace WHERE wing=$w AND room=$r AND drawer_id!=$best AND (expires_at IS NULL OR expires_at>$now)";
            delCmd.Parameters.AddWithValue("$w", wing); delCmd.Parameters.AddWithValue("$r", room); delCmd.Parameters.AddWithValue("$best", best);
            delCmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var deleted = await delCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            tx.Commit();
            if (deleted > 0) _logger?.LogDebug("Consolidated {Count} in {W}/{R}", deleted, wing, room);
            return deleted;
        }
        catch { tx.Rollback(); throw; }
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

    public async Task<IReadOnlyList<Drawer>> GetEssentialMomentsAsync(int maxCount = 10, string? agentId = null)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        var sql = "SELECT * FROM palace WHERE importance>=0.3 AND (expires_at IS NULL OR expires_at>$now)";
        if (agentId != null) sql += " AND agent_id=$agent";
        sql += " ORDER BY importance DESC,created_at DESC LIMIT $limit";
        cmd.CommandText = sql; cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$limit", maxCount);
        if (agentId != null) cmd.Parameters.AddWithValue("$agent", agentId);
        using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        var list = new List<Drawer>();
        while (await rdr.ReadAsync().ConfigureAwait(false)) list.Add(ReadDrawer(rdr));
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

    public async Task<IReadOnlyList<Drawer>> SearchByWingAsync(string wing, int maxCount = 10)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM palace WHERE wing=$w AND (expires_at IS NULL OR expires_at>$now) ORDER BY importance DESC,created_at DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$w", wing); cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); cmd.Parameters.AddWithValue("$limit", maxCount);
        using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        var list = new List<Drawer>();
        while (await rdr.ReadAsync().ConfigureAwait(false)) list.Add(ReadDrawer(rdr));
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

    public async Task<IReadOnlyList<Drawer>> SearchByRoomAsync(string room, string? agentId = null, int maxCount = 10)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        var sql = "SELECT * FROM palace WHERE room=$r AND (expires_at IS NULL OR expires_at>$now)";
        if (agentId != null) sql += " AND agent_id=$agent";
        sql += " ORDER BY created_at DESC LIMIT $limit";
        cmd.CommandText = sql; cmd.Parameters.AddWithValue("$r", room); cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$limit", maxCount);
        if (agentId != null) cmd.Parameters.AddWithValue("$agent", agentId);
        using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        var list = new List<Drawer>();
        while (await rdr.ReadAsync().ConfigureAwait(false)) list.Add(ReadDrawer(rdr));
        return list;
    }

    public IReadOnlyList<Drawer> SearchByWingExact(string wing, string? agentId = null)
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

    public async Task<IReadOnlyList<Drawer>> SearchByWingExactAsync(string wing, string? agentId = null)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        var sql = "SELECT * FROM palace WHERE wing=$w AND (expires_at IS NULL OR expires_at>$now)";
        if (agentId != null) sql += " AND agent_id=$agent";
        sql += " ORDER BY created_at DESC";
        cmd.CommandText = sql; cmd.Parameters.AddWithValue("$w", wing); cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (agentId != null) cmd.Parameters.AddWithValue("$agent", agentId);
        using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        var list = new List<Drawer>();
        while (await rdr.ReadAsync().ConfigureAwait(false)) list.Add(ReadDrawer(rdr));
        return list;
    }

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

    public async Task<IReadOnlyList<Drawer>> GetAllDrawersAsync()
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM palace WHERE (expires_at IS NULL OR expires_at>$now) ORDER BY created_at DESC LIMIT 200";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        var list = new List<Drawer>();
        while (await rdr.ReadAsync().ConfigureAwait(false)) list.Add(ReadDrawer(rdr));
        return list;
    }

    public bool DeleteDrawer(string wing, string room, string drawerId)
        => DeleteDrawer(drawerId);

    public async Task<bool> DeleteDrawerAsync(string drawerId)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM palace WHERE drawer_id=$id";
        cmd.Parameters.AddWithValue("$id", drawerId);
        var deleted = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false) > 0;
        if (deleted)
        {
            _removed.TryAdd(drawerId, 1);
            CleanupRemovedIfNeeded();
            if (_removed.Count > _hnsw.Count / 4 && _hnsw.Count > 100)
                _ = TriggerHnswRebuildAsync();
        }
        return deleted;
    }

    public async Task<bool> UpdateDrawerFieldsAsync(string wing, string room, string drawerId,
        Dictionary<string, object>? metadata = null, double? importance = null)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        var sets = new List<string>();
        if (metadata != null) { cmd.Parameters.AddWithValue("$meta", JsonSerializer.Serialize(metadata, JsonOpts)); sets.Add("metadata=$meta"); }
        if (importance.HasValue) { cmd.Parameters.AddWithValue("$imp", importance.Value); sets.Add("importance=$imp"); }
        if (sets.Count == 0) return false;
        cmd.CommandText = $"UPDATE palace SET {string.Join(",", sets)} WHERE wing=$w AND room=$r AND drawer_id=$id";
        cmd.Parameters.AddWithValue("$w", wing); cmd.Parameters.AddWithValue("$r", room); cmd.Parameters.AddWithValue("$id", drawerId);
        return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false) > 0;
    }

    public bool DeleteDrawer(string drawerId)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM palace WHERE drawer_id=$id";
        cmd.Parameters.AddWithValue("$id", drawerId);
        var deleted = cmd.ExecuteNonQuery() > 0;
        if (deleted)
        {
            _removed.TryAdd(drawerId, 1);
            CleanupRemovedIfNeeded();
            if (_removed.Count > _hnsw.Count / 4 && _hnsw.Count > 100)
                _ = TriggerHnswRebuildAsync();
        }
        return deleted;
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

    public async Task<int> DeleteWingRoomAsync(string wing, string room)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM palace WHERE wing=$w AND room=$r";
        cmd.Parameters.AddWithValue("$w", wing); cmd.Parameters.AddWithValue("$r", room);
        return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

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

    public async Task RecordAccessAsync(string drawerId)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE palace SET access_count=access_count+1, last_accessed_at=$now WHERE drawer_id=$id";
        cmd.Parameters.AddWithValue("$id", drawerId);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public void RecordAccessBatch(IEnumerable<string> drawerIds)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE palace SET access_count=access_count+1, last_accessed_at=$now WHERE drawer_id=$id";
        var nowParam = cmd.Parameters.Add("$now", SqliteType.Integer);
        var idParam = cmd.Parameters.Add("$id", SqliteType.Text);
        nowParam.Value = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var id in drawerIds)
        {
            idParam.Value = id;
            cmd.ExecuteNonQuery();
        }
    }

    public async Task RecordAccessBatchAsync(IEnumerable<string> drawerIds)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE palace SET access_count=access_count+1, last_accessed_at=$now WHERE drawer_id=$id";
        var nowParam = cmd.Parameters.Add("$now", SqliteType.Integer);
        var idParam = cmd.Parameters.Add("$id", SqliteType.Text);
        nowParam.Value = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var id in drawerIds)
        {
            idParam.Value = id;
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
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
            using var pragmaCmd = conn.CreateCommand();
            pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000";
            pragmaCmd.ExecuteNonQuery();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = Schema;
            cmd.ExecuteNonQuery();
            try
            {
                using var migCmd = conn.CreateCommand();
                migCmd.CommandText = "ALTER TABLE palace ADD COLUMN access_count INTEGER NOT NULL DEFAULT 0";
                migCmd.ExecuteNonQuery();
            }
            catch { }
            try
            {
                using var migCmd = conn.CreateCommand();
                migCmd.CommandText = "ALTER TABLE palace ADD COLUMN last_accessed_at INTEGER";
                migCmd.ExecuteNonQuery();
            }
            catch { }
            try
            {
                using var ftsCmd = conn.CreateCommand();
                ftsCmd.CommandText = FtsSchema;
                ftsCmd.ExecuteNonQuery();
            }
            catch { }
            _schemaReady = true;
        }
    }

    private static double CosineSimilarity(float[] a, float[] b) => VectorMath.CosineSimilarity(a.AsSpan(), b.AsSpan());

    internal static Drawer ReadDrawer(SqliteDataReader r)
    {
        var baseDrawer = new Drawer(
            r.GetString(0), r.GetString(1), r.GetString(2),
            r.IsDBNull(3) ? "assistant" : r.GetString(3), r.GetString(4),
            r.IsDBNull(5) ? null : DeserializeEmb(r, 5), r.GetInt64(6),
            r.IsDBNull(7) ? null : r.GetInt64(7), r.IsDBNull(8) ? 0.5 : r.GetDouble(8),
            r.IsDBNull(9) ? "default" : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10));
        try
        {
            var d = baseDrawer with
            {
                AccessCount = r.IsDBNull(11) ? 0 : r.GetInt32(11),
                LastAccessedAt = r.IsDBNull(12) ? null : r.GetInt64(12),
                Principal = r.FieldCount > 13 && !r.IsDBNull(13) ? r.GetString(13) : null,
                Scope = r.FieldCount > 14 && !r.IsDBNull(14) ? r.GetString(14) : "shared",
            };
            return d;
        }
        catch { return baseDrawer; }
    }

    private static float[]? DeserializeEmb(SqliteDataReader r, int c)
    {
        if (r.IsDBNull(c)) return null;
        var bytes = (byte[])r[c];
        if (bytes.Length == VectorQuantizer.PackedByteCount)
            return VectorQuantizer.DequantizeFromBytes(bytes);
        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    public async Task<int> ForgetAsync(string? drawerId = null, string? room = null,
        string? principal = null, string? scope = null, bool forgetAll = false)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();

        var conditions = new List<string>();
        if (drawerId != null) { conditions.Add("drawer_id = $id"); cmd.Parameters.AddWithValue("$id", drawerId); }
        if (room != null) { conditions.Add("room = $room"); cmd.Parameters.AddWithValue("$room", room); }
        if (principal != null) { conditions.Add("principal = $principal"); cmd.Parameters.AddWithValue("$principal", principal); }
        if (scope != null) { conditions.Add("scope = $scope"); cmd.Parameters.AddWithValue("$scope", scope); }

        if (conditions.Count == 0 && !forgetAll)
            return 0;

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        cmd.CommandText = $"DELETE FROM palace {where}";
        var count = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        _logger?.LogInformation("PalaceStore.ForgetAsync: deleted {Count} entries ({Criteria})", count,
            string.Join(", ", conditions));
        return count;
    }

    public async Task<int> PurgeExpiredAsync()
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM palace WHERE expires_at IS NOT NULL AND expires_at <= $now";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var count = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        if (count > 0)
            _logger?.LogInformation("PalaceStore.PurgeExpiredAsync: purged {Count} expired entries", count);
        return count;
    }

    public void Dispose()
    {
        _hnsw?.Dispose();
        _hnswLock?.Dispose();
    }
}
