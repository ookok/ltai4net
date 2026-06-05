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
            wing        TEXT    NOT NULL,
            room        TEXT    NOT NULL,
            drawer_id   TEXT    NOT NULL,
            role        TEXT    NOT NULL DEFAULT 'assistant',
            content     TEXT    NOT NULL,
            embedding   BLOB,
            created_at  INTEGER NOT NULL,
            expires_at  INTEGER,
            importance  REAL    NOT NULL DEFAULT 0.5,
            agent_id    TEXT    NOT NULL DEFAULT 'default',
            metadata    TEXT,
            PRIMARY KEY (wing, room, drawer_id)
        );
        CREATE INDEX IF NOT EXISTS idx_palace_wing_room ON palace(wing, room);
        CREATE INDEX IF NOT EXISTS idx_palace_agent ON palace(agent_id);
        CREATE INDEX IF NOT EXISTS idx_palace_created ON palace(created_at DESC);
        CREATE INDEX IF NOT EXISTS idx_palace_expires ON palace(expires_at);
        """;

    private const string MigrateExpiresAt = """
        SELECT COUNT(*) AS cnt FROM pragma_table_info('palace') WHERE name = 'expires_at';
        """;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly EmbeddingClient _embedder;
    private readonly string _connectionString;
    private readonly ILogger<PalaceStore>? _logger;
    private bool _schemaReady;
    private readonly object _gate = new();

    public PalaceStore(
        EmbeddingClient embedder,
        string dbPath,
        ILogger<PalaceStore>? logger = null)
    {
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _logger = logger;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public record Drawer(string Wing, string Room, string DrawerId, string Role, string Content,
        float[]? Embedding, long CreatedAt, long? ExpiresAt, double Importance, string AgentId, string? Metadata);

    /// <summary>Default TTL for memory entries (30 days in ms). Pass null or 0 to StoreAsync for no expiration.</summary>
    public const long DefaultTtlMs = 30L * 24 * 60 * 60 * 1000;

    public async Task<string> StoreAsync(string wing, string room, string content,
        string role = "assistant", double importance = 0.5, string? agentId = null,
        Dictionary<string, object>? metadata = null, long? ttlMs = null)
    {
        EnsureSchema();
        var drawerId = Guid.NewGuid().ToString("n");
        var vec = await _embedder.GenerateAsync(content, CancellationToken.None).ConfigureAwait(false);
        var metaJson = metadata != null ? JsonSerializer.Serialize(metadata, JsonOpts) : null;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expiresAt = ttlMs.HasValue ? now + ttlMs.Value : (long?)null;

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO palace(wing, room, drawer_id, role, content, embedding,
                created_at, expires_at, importance, agent_id, metadata)
            VALUES($wing, $room, $id, $role, $content, $emb, $ts, $exp, $imp, $agent, $meta)
            """;
        cmd.Parameters.AddWithValue("$wing", wing);
        cmd.Parameters.AddWithValue("$room", room);
        cmd.Parameters.AddWithValue("$id", drawerId);
        cmd.Parameters.AddWithValue("$role", role);
        cmd.Parameters.AddWithValue("$content", content);
        cmd.Parameters.AddWithValue("$emb", SerializeVector(vec));
        cmd.Parameters.AddWithValue("$ts", now);
        cmd.Parameters.AddWithValue("$exp", (object?)expiresAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$imp", importance);
        cmd.Parameters.AddWithValue("$agent", agentId ?? "default");
        cmd.Parameters.AddWithValue("$meta", (object?)metaJson ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        tx.Commit();
        _logger?.LogDebug("PalaceStore: stored drawer {Id} in {Wing}/{Room}", drawerId, wing, room);
        return drawerId;
    }

    public IReadOnlyList<Drawer> SearchByWing(string wing, int topK = 10)
    {
        EnsureSchema();
        var now = NowMs();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM palace WHERE wing = $wing AND " + LiveFilter + "ORDER BY importance DESC, created_at DESC LIMIT $k";
        cmd.Parameters.AddWithValue("$wing", wing);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$k", topK);
        return ReadDrawers(cmd);
    }

    public IReadOnlyList<Drawer> SearchByRoom(string wing, string room, int topK = 10)
    {
        EnsureSchema();
        var now = NowMs();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM palace WHERE wing = $wing AND room = $room AND " + LiveFilter + "ORDER BY importance DESC, created_at DESC LIMIT $k";
        cmd.Parameters.AddWithValue("$wing", wing);
        cmd.Parameters.AddWithValue("$room", room);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$k", topK);
        return ReadDrawers(cmd);
    }

    public async IAsyncEnumerable<(Drawer Drawer, double Score)> SemanticSearchAsync(
        float[] queryVec, int topK = 5, string? wing = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureSchema();
        var now = NowMs();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        // Scan ALL entries with embeddings — not just the most recent N.
        // Relevancy via full cosine scan instead of recency proxy.
        // For typical palace sizes (100-10000), this adds 2-50ms per query.
        // TODO: replace with HNSW index when palace exceeds 50000 entries.
        if (wing != null)
        {
            cmd.CommandText = "SELECT * FROM palace WHERE wing = $wing AND embedding IS NOT NULL AND " + LiveFilter;
            cmd.Parameters.AddWithValue("$wing", wing);
        }
        else
        {
            cmd.CommandText = "SELECT * FROM palace WHERE embedding IS NOT NULL AND " + LiveFilter;
        }
        cmd.Parameters.AddWithValue("$now", now);

        var results = new List<(Drawer d, double sim)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var d = ReadDrawer(reader);
            if (d.Embedding is null) continue;
            var sim = CosineSimilarity(queryVec, d.Embedding);
            results.Add((d, sim));
        }

        foreach (var hit in results.OrderByDescending(r => r.sim).Take(topK))
            yield return hit;
    }

    public IReadOnlyList<Drawer> GetEssentialMoments(int topK = 15, string? agentId = null)
    {
        EnsureSchema();
        var now = NowMs();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$k", topK);
        if (agentId != null)
        {
            cmd.CommandText = "SELECT * FROM palace WHERE agent_id = $agent AND " + LiveFilter + "ORDER BY importance DESC, created_at DESC LIMIT $k";
            cmd.Parameters.AddWithValue("$agent", agentId);
        }
        else
        {
            cmd.CommandText = "SELECT * FROM palace WHERE " + LiveFilter + "ORDER BY importance DESC, created_at DESC LIMIT $k";
        }
        return ReadDrawers(cmd);
    }

    public IReadOnlyList<Drawer> GetAgentDiary(string agentId, int topK = 10)
    {
        return GetEssentialMoments(topK, agentId);
    }

    public bool DeleteDrawer(string wing, string room, string drawerId)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM palace WHERE wing = $wing AND room = $room AND drawer_id = $id";
        cmd.Parameters.AddWithValue("$wing", wing);
        cmd.Parameters.AddWithValue("$room", room);
        cmd.Parameters.AddWithValue("$id", drawerId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public int DeleteWingRoom(string wing, string room)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM palace WHERE wing = $wing AND room = $room";
        cmd.Parameters.AddWithValue("$wing", wing);
        cmd.Parameters.AddWithValue("$room", room);
        return cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<Drawer> SearchByRoomExact(string room, int topK = 20)
    {
        EnsureSchema();
        var now = NowMs();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM palace WHERE room = $room AND " + LiveFilter + "ORDER BY importance DESC, created_at DESC LIMIT $k";
        cmd.Parameters.AddWithValue("$room", room);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$k", topK);
        return ReadDrawers(cmd);
    }

    public IReadOnlyList<string> ListRooms(string? wing = null)
    {
        EnsureSchema();
        var now = NowMs();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.Parameters.AddWithValue("$now", now);
        if (wing != null)
        {
            cmd.CommandText = "SELECT DISTINCT room FROM palace WHERE wing = $wing AND " + LiveFilter + "ORDER BY room";
            cmd.Parameters.AddWithValue("$wing", wing);
        }
        else
        {
            cmd.CommandText = "SELECT DISTINCT room FROM palace WHERE " + LiveFilter + "ORDER BY room";
        }
        var rooms = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rooms.Add(reader.GetString(0));
        return rooms;
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text)
    {
        return await _embedder.GenerateAsync(text, CancellationToken.None).ConfigureAwait(false);
    }

    public int Count()
    {
        EnsureSchema();
        var now = NowMs();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM palace WHERE " + LiveFilter;
        cmd.Parameters.AddWithValue("$now", now);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public IReadOnlyList<string> ListWings()
    {
        EnsureSchema();
        var now = NowMs();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT wing FROM palace WHERE " + LiveFilter + "ORDER BY wing";
        cmd.Parameters.AddWithValue("$now", now);
        var wings = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) wings.Add(reader.GetString(0));
        return wings;
    }

    /// <summary>Return all non-expired drawers.</summary>
    public IReadOnlyList<Drawer> GetAllDrawers()
    {
        EnsureSchema();
        var now = NowMs();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM palace WHERE " + LiveFilter + " ORDER BY wing, room, created_at DESC";
        cmd.Parameters.AddWithValue("$now", now);
        return ReadDrawers(cmd);
    }

    /// <summary>
    /// Snapshot all non-expired memory entries to <c>.livingtree/memories/&lt;sessionId&gt;.json</c>
    /// via LocalVersionRepo. Returns the commit SHA.
    /// </summary>
    public string SnapshotToRepo(string sessionId)
    {
        var drawers = GetAllDrawers();
        var json = JsonSerializer.Serialize(
            drawers.Select(d => new
            {
                d.Wing, d.Room, d.DrawerId, d.Role, d.Content,
                d.CreatedAt, d.ExpiresAt, d.Importance, d.AgentId, d.Metadata
            }), JsonOpts);
        return LocalVersionRepo.AtomicCommit(
            $"memories/{sessionId}.json", json,
            $"💾 Palace snapshot: {sessionId} — {drawers.Count} entries");
    }

    /// <summary>Remove all expired entries. Returns count of deleted rows.</summary>
    public int CleanupExpired()
    {
        EnsureSchema();
        var now = NowMs();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM palace WHERE expires_at IS NOT NULL AND expires_at <= $now";
        cmd.Parameters.AddWithValue("$now", now);
        var count = cmd.ExecuteNonQuery();
        if (count > 0)
            _logger?.LogInformation("PalaceStore: cleaned up {Count} expired entries", count);
        return count;
    }

    private const string LiveFilter = " (expires_at IS NULL OR expires_at > $now) ";

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
            // F5: Add expires_at column if missing (migration from older version)
            using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = MigrateExpiresAt;
            var count = (long)(checkCmd.ExecuteScalar() ?? 0);
            if (count == 0)
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE palace ADD COLUMN expires_at INTEGER;";
                alterCmd.ExecuteNonQuery();
            }
            _schemaReady = true;
        }
    }

    private long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static List<Drawer> ReadDrawers(SqliteCommand cmd)
    {
        var list = new List<Drawer>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(ReadDrawer(reader));
        return list;
    }

    private static Drawer ReadDrawer(SqliteDataReader r) => new(
        Wing: r.GetString(0),
        Room: r.GetString(1),
        DrawerId: r.GetString(2),
        Role: r.GetString(3),
        Content: r.GetString(4),
        Embedding: r.IsDBNull(5) ? null : DeserializeVector((byte[])r.GetValue(5)),
        CreatedAt: r.GetInt64(6),
        ExpiresAt: r.IsDBNull(7) ? null : r.GetInt64(7),
        Importance: r.GetDouble(8),
        AgentId: r.GetString(9),
        Metadata: r.IsDBNull(10) ? null : r.GetString(10));

    private static byte[] SerializeVector(float[] v)
    {
        var bytes = new byte[v.Length * sizeof(float)];
        Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] DeserializeVector(byte[] bytes)
    {
        var v = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, v, 0, bytes.Length);
        return v;
    }

    private static float CosineSimilarity(float[] a, float[] b)
        => LTAI.AI.VectorMath.CosineSimilarity(a.AsSpan(), b.AsSpan());
}
