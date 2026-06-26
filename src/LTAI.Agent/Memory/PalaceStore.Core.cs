// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  PalaceStore — Core CRUD: store, retrieve, delete, touch, count
// ═══════════════════════════════════════════════════════════════

using System.Text.Json;
using LTAI.Agent.Vector;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

partial class PalaceStore
{
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

        if (vec.Length == VectorQuantizer.Dim)
        {
            if (_writeQueue != null)
                _writeQueue.Enqueue(vec, drawerId);
            else
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
        }

        var currentCount = await CountAsync().ConfigureAwait(false);
        if (currentCount > _maxEntries * 1.1)
            _ = Task.Run(async () =>
            {
                try { await TrimAsync(_maxEntries).ConfigureAwait(false); }
                catch (Exception ex) { _logger?.LogWarning(ex, "PalaceStore background TrimAsync failed"); }
            });

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
        catch { }

        return drawerId;
    }

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
}
