// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  PalaceStore — Query methods: recent, important, wings, rooms
// ═══════════════════════════════════════════════════════════════

using Microsoft.Data.Sqlite;

namespace LTAI.Agent.Memory;

partial class PalaceStore
{
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
}
