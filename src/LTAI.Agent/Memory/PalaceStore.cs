using System.Text.Json;
using LTAI.AI;
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
            importance  REAL    NOT NULL DEFAULT 0.5,
            agent_id    TEXT    NOT NULL DEFAULT 'default',
            metadata    TEXT,
            PRIMARY KEY (wing, room, drawer_id)
        );
        CREATE INDEX IF NOT EXISTS idx_palace_wing_room ON palace(wing, room);
        CREATE INDEX IF NOT EXISTS idx_palace_agent ON palace(agent_id);
        CREATE INDEX IF NOT EXISTS idx_palace_created ON palace(created_at DESC);
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
        float[]? Embedding, long CreatedAt, double Importance, string AgentId, string? Metadata);

    public async Task<string> StoreAsync(string wing, string room, string content,
        string role = "assistant", double importance = 0.5, string? agentId = null,
        Dictionary<string, object>? metadata = null)
    {
        EnsureSchema();
        var drawerId = Guid.NewGuid().ToString("n");
        var vec = await _embedder.GenerateAsync(content, CancellationToken.None).ConfigureAwait(false);
        var metaJson = metadata != null ? JsonSerializer.Serialize(metadata, JsonOpts) : null;

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO palace(wing, room, drawer_id, role, content, embedding,
                created_at, importance, agent_id, metadata)
            VALUES($wing, $room, $id, $role, $content, $emb, $ts, $imp, $agent, $meta)
            """;
        cmd.Parameters.AddWithValue("$wing", wing);
        cmd.Parameters.AddWithValue("$room", room);
        cmd.Parameters.AddWithValue("$id", drawerId);
        cmd.Parameters.AddWithValue("$role", role);
        cmd.Parameters.AddWithValue("$content", content);
        cmd.Parameters.AddWithValue("$emb", SerializeVector(vec));
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
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
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM palace WHERE wing = $wing ORDER BY importance DESC, created_at DESC LIMIT $k";
        cmd.Parameters.AddWithValue("$wing", wing);
        cmd.Parameters.AddWithValue("$k", topK);
        return ReadDrawers(cmd);
    }

    public IReadOnlyList<Drawer> SearchByRoom(string wing, string room, int topK = 10)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM palace WHERE wing = $wing AND room = $room ORDER BY importance DESC, created_at DESC LIMIT $k";
        cmd.Parameters.AddWithValue("$wing", wing);
        cmd.Parameters.AddWithValue("$room", room);
        cmd.Parameters.AddWithValue("$k", topK);
        return ReadDrawers(cmd);
    }

    public async IAsyncEnumerable<(Drawer Drawer, double Score)> SemanticSearchAsync(
        float[] queryVec, int topK = 5, string? wing = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        // Scan ALL entries with embeddings — not just the most recent N.
        // Relevancy via full cosine scan instead of recency proxy.
        // For typical palace sizes (100-10000), this adds 2-50ms per query.
        // TODO: replace with HNSW index when palace exceeds 50000 entries.
        if (wing != null)
        {
            cmd.CommandText = "SELECT * FROM palace WHERE wing = $wing AND embedding IS NOT NULL";
            cmd.Parameters.AddWithValue("$wing", wing);
        }
        else
        {
            cmd.CommandText = "SELECT * FROM palace WHERE embedding IS NOT NULL";
        }

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
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        if (agentId != null)
        {
            cmd.CommandText = "SELECT * FROM palace WHERE agent_id = $agent ORDER BY importance DESC, created_at DESC LIMIT $k";
            cmd.Parameters.AddWithValue("$agent", agentId);
        }
        else
        {
            cmd.CommandText = "SELECT * FROM palace ORDER BY importance DESC, created_at DESC LIMIT $k";
        }
        cmd.Parameters.AddWithValue("$k", topK);
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
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM palace WHERE room = $room ORDER BY importance DESC, created_at DESC LIMIT $k";
        cmd.Parameters.AddWithValue("$room", room);
        cmd.Parameters.AddWithValue("$k", topK);
        return ReadDrawers(cmd);
    }

    public IReadOnlyList<string> ListRooms(string? wing = null)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        if (wing != null)
        {
            cmd.CommandText = "SELECT DISTINCT room FROM palace WHERE wing = $wing ORDER BY room";
            cmd.Parameters.AddWithValue("$wing", wing);
        }
        else
        {
            cmd.CommandText = "SELECT DISTINCT room FROM palace ORDER BY room";
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
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM palace";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public IReadOnlyList<string> ListWings()
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT wing FROM palace ORDER BY wing";
        var wings = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) wings.Add(reader.GetString(0));
        return wings;
    }

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
            _schemaReady = true;
        }
    }

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
        Importance: r.GetDouble(7),
        AgentId: r.GetString(8),
        Metadata: r.IsDBNull(9) ? null : r.GetString(9));

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
