// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  PalaceStore — structured long-term memory (shares kg.db)
//  TurboQuant 4-bit vectors (192B vs 1536B raw) + FTS5 BM25 + HNSW.
// ═══════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using System.Text.Json;
using LTAI.AI;
using LTAI.Agent.Vector;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

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

        // EvoEmbedding segment-batching: initialize batch write queue
        _writeQueue = new MemoryWriteQueue(_hnsw, _hnswMap, _hnswRev, _hnswLock, _logger,
            batchSize: MemoryWriteQueue.DefaultBatchSize,
            flushIntervalMs: MemoryWriteQueue.DefaultFlushIntervalMs);
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

    public async Task<float[]> GenerateEmbeddingAsync(string text)
        => await _embedder.GenerateAsync(text, CancellationToken.None).ConfigureAwait(false);

    public void Dispose()
    {
        _writeQueue?.Dispose();
        _hnsw?.Dispose();
        _hnswLock?.Dispose();
    }

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

    internal static float[]? DeserializeEmb(SqliteDataReader r, int c)
    {
        if (r.IsDBNull(c)) return null;
        var bytes = (byte[])r[c];
        if (bytes.Length == VectorQuantizer.PackedByteCount)
            return VectorQuantizer.DequantizeFromBytes(bytes);
        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }
}
