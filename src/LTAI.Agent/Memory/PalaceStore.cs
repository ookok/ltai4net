// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  PalaceStore — structured long-term memory (shares kg.db)
//  TurboQuant 4-bit vectors (192B vs 1536B raw) + FTS5 BM25 + HNSW.
// ═══════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using System.Linq;
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

    // HNSW vector index for sub-linear semantic search
    private readonly HnswIndex _hnsw = new(HnswOptions.Default);
    private readonly ConcurrentDictionary<int, string> _hnswMap = new();
    private readonly ConcurrentDictionary<string, int> _hnswRev = new();
    private readonly ConcurrentDictionary<string, byte> _removed = new();
    private int _hnswReady;
    private readonly SemaphoreSlim _hnswLock = new(1, 1);
    private string HnswSnapshotPath => _dbPath + ".hnsw";
    private long _lastRemovedCleanupMs;

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
        int AccessCount = 0, long? LastAccessedAt = null);

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
        Dictionary<string, object>? metadata = null, long? ttlMs = null)
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
            expiresAt = null; // permanent entry
        else
            expiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + DefaultTtlMs;

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO palace (wing,room,drawer_id,role,content,embedding,created_at,expires_at,importance,agent_id,metadata) VALUES ($w,$r,$id,$role,$c,$emb,$now,$exp,$imp,$agent,$meta)";
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

        // Evict oldest entries if exceeding limit (background, fire-and-forget)
        var currentCount = await CountAsync().ConfigureAwait(false);
        if (currentCount > _maxEntries * 1.1)
            _ = Task.Run(async () =>
            {
                try { await TrimAsync(_maxEntries).ConfigureAwait(false); }
                catch (Exception ex) { _logger?.LogWarning(ex, "PalaceStore background TrimAsync failed"); }
            });

        // ── Entity Surfacing (MeMo-inspired) ──
        // Automatically generate reverse QA pairs to mitigate the reversal curse.
        // For each stored entry, create "Who is X" and "What does X do" variants
        // so memory can be retrieved from both forward and backward directions.
        try
        {
            var entityPairs = SurfaceEntitiesFromContent(content, wing, room);
            foreach (var (q, a) in entityPairs)
            {
                // store as lower-importance companion entries in the same room
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

    /// <summary>Extract entity surfacing QA pairs from content (MeMo §4.1 Step 4).</summary>
    private static List<(string Question, string Answer)> SurfaceEntitiesFromContent(
        string content, string wing, string room)
    {
        var pairs = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(content)) return pairs;

        // Extract capitalized entities
        var entities = new HashSet<string>();
        foreach (Match m in EntityExtractPattern().Matches(content))
        {
            var entity = m.Groups[1].Value.Trim();
            if (entity.Length >= 3 && entity.Length <= 60)
                entities.Add(entity);
        }

        foreach (var entity in entities.Take(3))
        {
            // Find sentence describing this entity
            var sentences = content.Split(['.', '!', '?', '\n'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var sentence in sentences)
            {
                if (sentence.Contains(entity, StringComparison.OrdinalIgnoreCase))
                {
                    var desc = sentence.Trim();
                    if (desc.Length > entity.Length + 5)
                    {
                        if (desc.Length > 150) desc = desc[..147] + "...";
                        pairs.Add(($"Who or what is {entity}?",
                                   $"In {wing}/{room}: {entity} — {desc}"));
                        pairs.Add(($"What is {entity} known for?",
                                   $"{entity} relates to {wing}/{room}: {desc}"));
                        break;
                    }
                }
            }
        }

        return pairs;
    }

    [GeneratedRegex(@"\b([A-Z][a-z]+(?:\s+[A-Z][a-z]+)+)\b", RegexOptions.Compiled, 500)]
    private static partial Regex EntityExtractPattern();

    /// <summary>
    /// Trim to target count by evicting oldest + least-important entries.
    /// </summary>
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
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$limit", toEvict);
        var deleted = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        if (deleted > 0)
        {
            _logger?.LogInformation("PalaceStore: evicted {Count} oldest entries (limit={Max})", deleted, targetCount);
            _ = TriggerHnswRebuildAsync();
        }
    }

    public async Task<bool> TouchDrawerAsync(string wing, string room, string drawerId, double importance)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE palace SET importance=MAX(importance,$imp),expires_at=CASE WHEN expires_at IS NULL THEN NULL ELSE $exp END WHERE wing=$w AND room=$r AND drawer_id=$id";
        cmd.Parameters.AddWithValue("$imp", importance); cmd.Parameters.AddWithValue("$exp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + DefaultTtlMs);
        cmd.Parameters.AddWithValue("$w", wing); cmd.Parameters.AddWithValue("$r", room); cmd.Parameters.AddWithValue("$id", drawerId);
        return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false) > 0;
    }

    // Sync overload for backward compat
    public bool TouchDrawer(string wing, string room, string drawerId, double importance)
    {
        return TouchDrawerAsync(wing, room, drawerId, importance).GetAwaiter().GetResult();
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

    public async Task<Drawer?> GetDrawerAsync(string wing, string room, string drawerId)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM palace WHERE wing=$w AND room=$r AND drawer_id=$id AND (expires_at IS NULL OR expires_at>$now)";
        cmd.Parameters.AddWithValue("$w", wing); cmd.Parameters.AddWithValue("$r", room); cmd.Parameters.AddWithValue("$id", drawerId);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        return await rdr.ReadAsync().ConfigureAwait(false) ? ReadDrawer(rdr) : null;
    }

    public Drawer? GetDrawerById(string drawerId)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM palace WHERE drawer_id=$id AND (expires_at IS NULL OR expires_at>$now) LIMIT 1";
        cmd.Parameters.AddWithValue("$id", drawerId);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var rdr = cmd.ExecuteReader();
        return rdr.Read() ? ReadDrawer(rdr) : null;
    }

    public async Task<Drawer?> GetDrawerByIdAsync(string drawerId)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM palace WHERE drawer_id=$id AND (expires_at IS NULL OR expires_at>$now) LIMIT 1";
        cmd.Parameters.AddWithValue("$id", drawerId);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        return await rdr.ReadAsync().ConfigureAwait(false) ? ReadDrawer(rdr) : null;
    }

    public async IAsyncEnumerable<(Drawer Drawer, double Score)> SemanticSearchAsync(
        float[] queryVec, int topK = 5, string? wing = null, string? room = null)
    {
        EnsureSchema();
        _ = WarmupHnswAsync();

        if (Interlocked.CompareExchange(ref _hnswReady, 0, 0) == 1 && _hnsw.Count > 0)
        {
            var hnswResults = _hnsw.Search(queryVec, topK * 4);

            // Batch-validate candidates: collect IDs, then single SQL IN query
            var candidates = new List<(int Idx, double Distance, string DrawerId)>();
            foreach (var (idx, distance) in hnswResults)
            {
                if (candidates.Count >= topK * 3) break;
                if (!_hnswMap.TryGetValue(idx, out var did) || _removed.ContainsKey(did)) continue;
                candidates.Add((idx, distance, did));
            }

            if (candidates.Count > 0)
            {
                var drawersById = new Dictionary<string, Drawer>(StringComparer.OrdinalIgnoreCase);
                using var batchConn = new SqliteConnection(_connectionString);
                await batchConn.OpenAsync().ConfigureAwait(false);
                var placeholders = string.Join(",", candidates.Select((_, i) => $"@p{i}"));
                using var batchCmd = batchConn.CreateCommand();
                batchCmd.CommandText = $"SELECT * FROM palace WHERE drawer_id IN ({placeholders}) AND (expires_at IS NULL OR expires_at>$now)";
                batchCmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                for (int i = 0; i < candidates.Count; i++)
                    batchCmd.Parameters.AddWithValue($"@p{i}", candidates[i].DrawerId);

                using var batchRdr = await batchCmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await batchRdr.ReadAsync().ConfigureAwait(false))
                {
                    var d = ReadDrawer(batchRdr);
                    if ((wing == null || string.Equals(d.Wing, wing, StringComparison.OrdinalIgnoreCase))
                        && (room == null || string.Equals(d.Room, room, StringComparison.OrdinalIgnoreCase)))
                        drawersById[d.DrawerId] = d;
                }

                var scored = new List<(Drawer, double)>();
                foreach (var (_, distance, did) in candidates)
                {
                    if (drawersById.TryGetValue(did, out var d))
                        scored.Add((d, 1.0 - distance));
                    if (scored.Count >= topK) break;
                }

                scored.Sort((a, b) => b.Item2.CompareTo(a.Item2));
                var taken = 0;
                foreach (var hit in scored)
                {
                    if (taken >= topK) break;
                    RecordAccess(hit.Item1.DrawerId);
                    yield return hit;
                    taken++;
                }
                if (taken > 0) yield break;
            }
        }

        // Fallback: brute-force scan (HNSW not ready or empty)
        var fallback = new List<(Drawer, double)>();
        using var fallbackConn = new SqliteConnection(_connectionString);
        await fallbackConn.OpenAsync().ConfigureAwait(false);
        using var fallbackCmd = fallbackConn.CreateCommand();
        var fbSql = "SELECT * FROM palace WHERE embedding IS NOT NULL AND (expires_at IS NULL OR expires_at>$now)";
        if (wing != null) fbSql += " AND wing=$w";
        if (room != null) fbSql += " AND room=$r";
        fallbackCmd.CommandText = fbSql;
        fallbackCmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (wing != null) fallbackCmd.Parameters.AddWithValue("$w", wing);
        if (room != null) fallbackCmd.Parameters.AddWithValue("$r", room);
        using var fbRdr = await fallbackCmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (fbRdr.Read())
        {
            var drawer = ReadDrawer(fbRdr);
            if (drawer.Embedding is { Length: > 0 })
                fallback.Add((drawer, CosineSimilarity(queryVec, drawer.Embedding)));
        }
        foreach (var hit in fallback.OrderByDescending(x => x.Item2).Take(topK))
        {
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

    public int PurgeExpired()
    {
        EnsureSchema();
        // Collect expired IDs before deleting (so we can clean HNSW)
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

        // Mark expired as removed in HNSW maps
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

    /// <summary>Delete a single drawer by its ID (wing+room+drawerId overload for MemoryBrowser).</summary>
    public bool DeleteDrawer(string wing, string room, string drawerId)
        => DeleteDrawer(drawerId);

    /// <summary>Delete a single drawer by its ID.</summary>
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

    // Sync compat
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

    private async Task TriggerHnswRebuildAsync()
    {
        try { await RebuildHnswAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger?.LogWarning(ex, "PalaceStore background RebuildHnswAsync failed"); }
    }

    /// <summary>
    /// Periodically clean up _removed dictionary entries that no longer exist in _hnswMap.
    /// Avoids unbounded growth when deletion rate is low.
    /// </summary>
    private void CleanupRemovedIfNeeded()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now - Interlocked.Read(ref _lastRemovedCleanupMs) < 300_000) return; // max every 5 min
        Interlocked.Exchange(ref _lastRemovedCleanupMs, now);

        var toRemove = new List<string>();
        foreach (var (key, _) in _removed)
            if (!_hnswRev.ContainsKey(key)) toRemove.Add(key);

        foreach (var key in toRemove)
            _removed.TryRemove(key, out _);
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

    /// <summary>Async version — preferred for hot paths.</summary>
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

    /// <summary>Batch record access for multiple drawers in a single connection.</summary>
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

    public async Task<IReadOnlyList<Drawer>> GetPopularDrawersAsync(int limit = 10)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM palace WHERE access_count>0 AND (expires_at IS NULL OR expires_at>$now) ORDER BY access_count DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$limit", limit);
        using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        var list = new List<Drawer>();
        while (await rdr.ReadAsync().ConfigureAwait(false)) list.Add(ReadDrawer(rdr));
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
            using var pragmaCmd = conn.CreateCommand();
            pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000";
            pragmaCmd.ExecuteNonQuery();
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
            // FTS5 full-text index for BM25 keyword search
            try
            {
                using var ftsCmd = conn.CreateCommand();
                ftsCmd.CommandText = FtsSchema;
                ftsCmd.ExecuteNonQuery();
            }
            catch { /* FTS5 already exists */ }
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
        // Read access tracking columns (if they exist in query result)
        try { return baseDrawer with { AccessCount = r.IsDBNull(11) ? 0 : r.GetInt32(11), LastAccessedAt = r.IsDBNull(12) ? null : r.GetInt64(12) }; }
        catch { return baseDrawer; }
    }

    private static float[]? DeserializeEmb(SqliteDataReader r, int c)
    {
        if (r.IsDBNull(c)) return null;
        var bytes = (byte[])r[c];
        // TurbQuant 4-bit packed (192 bytes for 384d) vs legacy raw float[] (1536 bytes)
        if (bytes.Length == VectorQuantizer.PackedByteCount)
            return VectorQuantizer.DequantizeFromBytes(bytes);
        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    /// <summary>
    /// FTS5 BM25 keyword search. Returns drawer IDs ordered by BM25 rank.
    /// </summary>
    public IReadOnlyList<(string DrawerId, double Bm25Score)> SearchFts(string query, int topK = 10,
        string? wing = null, string? room = null)
    {
        EnsureSchema();
        var results = new List<(string, double)>();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        var where = "";
        if (wing != null) where += " AND wing=@w";
        if (room != null) where += " AND room=@r";
        cmd.CommandText = $"SELECT drawer_id, bm25(palace_fts, 1.0, 0.75) AS rank FROM palace_fts WHERE palace_fts MATCH @q{where} ORDER BY rank LIMIT @k";
        cmd.Parameters.AddWithValue("@q", EscapeFts5(query));
        cmd.Parameters.AddWithValue("@k", topK);
        if (wing != null) cmd.Parameters.AddWithValue("@w", wing);
        if (room != null) cmd.Parameters.AddWithValue("@r", room);
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            var id = rdr.GetString(0);
            var score = rdr.IsDBNull(1) ? 0.0 : rdr.GetDouble(1);
            results.Add((id, score));
        }
        return results;
    }

    /// <summary>
    /// Sanitize user input for FTS5 MATCH. Uses proper phrase quoting.
    /// FTS5 special: * - ( ) " AND OR NOT NEAR
    /// </summary>
    private static string EscapeFts5(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "\"\"";
        var sanitized = raw
            .Replace("\"", "\"\"") // escape double quotes for FTS5
            .Replace("-", " ")
            .Replace("(", " ")
            .Replace(")", " ")
            .Replace("*", " ")
            .Trim();

        // Remove boolean operators as standalone tokens (not substrings)
        sanitized = Fts5OperatorPattern().Replace(sanitized, " ");
        sanitized = Regex.Replace(sanitized, @"\s+", " ").Trim();

        if (sanitized.Length == 0) return "\"\"";
        return $"\"{sanitized}\"";
    }

    [GeneratedRegex(@"\b(AND|OR|NOT|NEAR)\b", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Fts5OperatorPattern();

    /// <summary>
    /// RRF (Reciprocal Rank Fusion) hybrid search: combines FTS5 BM25 + HNSW semantic.
    /// </summary>
    public async Task<IReadOnlyList<(Drawer Drawer, double Score)>> HybridSearchAsync(
        float[] queryVec, string ftsQuery, int topK = 5, string? wing = null, string? room = null)
    {
        // Cache key: hash of vector prefix + fts query + wing + room + topK
        var cacheKey = $"{Convert.ToHexString(queryVec.Take(16).SelectMany(BitConverter.GetBytes).ToArray())}|{ftsQuery}|{wing}|{room}|{topK}";
        if (_searchCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.Expiry)
            return cached.Results;

        const int k = 60;
        var ftsResults = SearchFts(ftsQuery, k, wing, room);
        var semResults = new List<(string Id, double Score)>();
        await foreach (var (drawer, score) in SemanticSearchAsync(queryVec, k, wing, room).ConfigureAwait(false))
            semResults.Add((drawer.DrawerId, score));

        var rrf = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < ftsResults.Count; i++)
            rrf[ftsResults[i].DrawerId] = 1.0 / (i + 60);
        for (int i = 0; i < semResults.Count; i++)
        {
            var id = semResults[i].Id;
            rrf[id] = rrf.GetValueOrDefault(id) + 1.0 / (i + 60);
        }

        // ── Cross-Retrieval Majority Voting ──
        // Items retrieved by BOTH FTS5 + HNSW are high-confidence;
        // single-path items get a score penalty (same pattern as ToolRegistry).
        var ftsSet = new HashSet<string>(ftsResults.Select(r => r.DrawerId), StringComparer.OrdinalIgnoreCase);
        var semSet = new HashSet<string>(semResults.Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
        const double MajorityVotePenalty = 0.3;

        var topIds = rrf
            .Select(kvp => (id: kvp.Key, score: kvp.Value * (ftsSet.Contains(kvp.Key) && semSet.Contains(kvp.Key) ? 1.0 : MajorityVotePenalty)))
            .OrderByDescending(x => x.score)
            .Take(topK)
            .ToList();
        if (topIds.Count == 0) return [];

        // Batch load drawers via IN clause (single query instead of per-drawer)
        var scored = new List<(Drawer, double)>();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        var placeholders = string.Join(",", topIds.Select((_, i) => $"@p{i}"));
        using var batchCmd = conn.CreateCommand();
        batchCmd.CommandText = $"SELECT * FROM palace WHERE drawer_id IN ({placeholders}) AND (expires_at IS NULL OR expires_at>@now)";
        batchCmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        for (int i = 0; i < topIds.Count; i++)
            batchCmd.Parameters.AddWithValue($"@p{i}", topIds[i].id);

        var drawersById = new Dictionary<string, Drawer>(StringComparer.OrdinalIgnoreCase);
        using var batchRdr = await batchCmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await batchRdr.ReadAsync().ConfigureAwait(false))
        {
            var d = ReadDrawer(batchRdr);
            if ((wing == null || string.Equals(d.Wing, wing, StringComparison.OrdinalIgnoreCase))
                && (room == null || string.Equals(d.Room, room, StringComparison.OrdinalIgnoreCase)))
                drawersById[d.DrawerId] = d;
        }

        foreach (var (id, score) in topIds)
        {
            if (drawersById.TryGetValue(id, out var drawer))
            {
                scored.Add((drawer, score));
            }
        }
        RecordAccessBatch(scored.Select(s => s.Item1.DrawerId));
        _searchCache[cacheKey] = (DateTime.UtcNow + SearchCacheTtl, scored);
        // Prune expired entries on write (amortized cleanup)
        if (_searchCache.Count > 128)
        {
            var expired = _searchCache.Where(kv => kv.Value.Expiry < DateTime.UtcNow).Select(kv => kv.Key).Take(32).ToList();
            foreach (var key in expired) _searchCache.TryRemove(key, out _);
        }
        return scored;
    }

    /// <summary>Warm up the HNSW index on first use — tries snapshot, falls back to SQL rebuild.</summary>
    public Task WarmupHnswAsync()
    {
        if (Interlocked.CompareExchange(ref _hnswReady, 0, 0) == 1)
            return Task.CompletedTask;
        return WarmupHnswCoreAsync();
    }

    private async Task WarmupHnswCoreAsync()
    {
        await _hnswLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Double-check after acquiring lock
            if (Interlocked.CompareExchange(ref _hnswReady, 0, 0) == 1)
                return;

            // Try loading HNSW snapshot from disk (avoids full SQL scan on restart)
            var snapshotPath = HnswSnapshotPath;
            if (File.Exists(snapshotPath))
            {
                try
                {
                    _logger?.LogInformation("PalaceStore: loading HNSW snapshot from {Path}", snapshotPath);
                    await RebuildHnswFromSnapshotAsync(snapshotPath).ConfigureAwait(false);
                    Interlocked.Exchange(ref _hnswReady, 1);
                    _logger?.LogInformation("PalaceStore: HNSW snapshot loaded ({Count} nodes)", _hnsw.Count);
                    return;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "PalaceStore: HNSW snapshot load failed, rebuilding from SQL");
                    try { File.Delete(snapshotPath); } catch
                    {
                        _logger?.LogWarning("Swallowing exception in PalaceStore.cs");
                    }
                }
            }
            await RebuildHnswCoreAsync().ConfigureAwait(false);
        }
        finally { _hnswLock.Release(); }
    }

    private async Task RebuildHnswFromSnapshotAsync(string snapshotPath)
    {
        var json = await File.ReadAllTextAsync(snapshotPath).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        _hnswMap.Clear();
        _hnswRev.Clear();
        _removed.Clear();
        _hnsw.Rebuild([]); // clear existing

        var nodes = root.GetProperty("Nodes").EnumerateArray();
        var vectors = new List<(float[] vec, string id)>();
        foreach (var nodeEl in nodes)
        {
            var id = nodeEl.GetProperty("Id").GetString()!;
            var data = Convert.FromBase64String(nodeEl.GetProperty("Data").GetString()!);
            var vec = VectorQuantizer.DequantizeFromBytes(data);
            vectors.Add((vec, id));
        }
        _hnsw.Rebuild(vectors.Select(v => (ReadOnlyMemory<float>)v.vec));
        for (int i = 0; i < vectors.Count; i++)
        {
            _hnswMap[i] = vectors[i].id;
            _hnswRev[vectors[i].id] = i;
        }
    }

    /// <summary>Rebuild the HNSW index from all non-expired palace entries with embeddings.</summary>
    public async Task RebuildHnswAsync()
    {
        await _hnswLock.WaitAsync().ConfigureAwait(false);
        try { await RebuildHnswCoreAsync().ConfigureAwait(false); }
        finally { _hnswLock.Release(); }
    }

    private async Task RebuildHnswCoreAsync()
    {
        EnsureSchema();
        Interlocked.Exchange(ref _hnswReady, 0);
        _hnswMap.Clear();
        _hnswRev.Clear();
        _removed.Clear();

        var vectors = new List<(float[] vec, string id)>();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT drawer_id, embedding FROM palace WHERE embedding IS NOT NULL AND (expires_at IS NULL OR expires_at>$now)";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await rdr.ReadAsync().ConfigureAwait(false))
        {
            var id = rdr.GetString(0);
            var emb = DeserializeEmb(rdr, 1);
            if (emb != null && emb.Length == VectorQuantizer.Dim)
                vectors.Add((emb, id));
        }

        _logger?.LogInformation("PalaceStore: rebuilding HNSW index with {Count} vectors", vectors.Count);
        _hnsw.Rebuild(vectors.Select(v => (ReadOnlyMemory<float>)v.vec));
        _hnswMap.Clear();
        _hnswRev.Clear();
        _removed.Clear();
        for (int i = 0; i < vectors.Count; i++)
        {
            _hnswMap[i] = vectors[i].id;
            _hnswRev[vectors[i].id] = i;
        }
        Interlocked.Exchange(ref _hnswReady, 1);
        _logger?.LogInformation("PalaceStore: HNSW index rebuilt ({Count} nodes)", vectors.Count);

        // Save snapshot for fast restart
        _ = Task.Run(() =>
        {
            try { SaveHnswSnapshot(); }
            catch (Exception ex) { _logger?.LogWarning(ex, "PalaceStore: HNSW snapshot save failed"); }
        });
    }

    private void SaveHnswSnapshot()
    {
        var snapshotPath = HnswSnapshotPath;
        var tmpPath = snapshotPath + ".tmp";

        // Batch-load all embeddings in a single query (avoid O(n) connections)
        var embBlobs = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using (var conn = new SqliteConnection(_connectionString))
        {
            conn.Open();
            var ids = _hnswMap.Values.ToList();
            if (ids.Count == 0) return;
            var placeholders = string.Join(",", ids.Select((_, i) => $"@p{i}"));
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT drawer_id, embedding FROM palace WHERE drawer_id IN ({placeholders})";
            for (int i = 0; i < ids.Count; i++)
                cmd.Parameters.AddWithValue($"@p{i}", ids[i]);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                var id = rdr.GetString(0);
                if (!rdr.IsDBNull(1))
                    embBlobs[id] = (byte[])rdr[1];
            }
        }

        using var stream = File.Create(tmpPath);
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteStartArray("Nodes");
        foreach (var (_, drawerId) in _hnswMap)
        {
            writer.WriteStartObject();
            writer.WriteString("Id", drawerId);
            writer.WriteString("Data", embBlobs.TryGetValue(drawerId, out var blob) ? Convert.ToBase64String(blob) : "");
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        File.Move(tmpPath, snapshotPath, true);
    }

    public void Dispose()
    {
        _hnsw?.Dispose();
        _hnswLock?.Dispose();
    }
}
