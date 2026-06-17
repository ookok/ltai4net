using LTAI.Agent.Vector;
using Microsoft.Data.Sqlite;

namespace LTAI.Agent.Memory;

public sealed class FallbackRetriever
{
    private readonly PalaceStore _store;
    private readonly int _topK;

    public FallbackRetriever(PalaceStore store, int topK = 5)
    {
        _store = store;
        _topK = topK;
    }

    public enum FallbackLevel { Hybrid = 0, Dense = 1, Fts = 2, Like = 3, None = 4 }

    public record FallbackResult(List<(PalaceStore.Drawer Drawer, double Score)> Results, FallbackLevel LevelUsed);

    public async Task<FallbackResult> RetrieveAsync(
        float[] queryVec, string ftsQuery, string? wing = null, string? room = null,
        double minScore = 0.3)
    {
        // Level 0: Hybrid (RRF fusion of BM25 + dense)
        var hybrid = await _store.HybridSearchAsync(queryVec, ftsQuery, _topK, wing, room).ConfigureAwait(false);
        var filtered = hybrid.Where(h => h.Score >= minScore).ToList();
        if (filtered.Count > 0)
            return new FallbackResult(filtered, FallbackLevel.Hybrid);

        // Level 1: Dense-only (semantic search)
        var dense = new List<(PalaceStore.Drawer, double)>();
        await foreach (var (drawer, score) in _store.SemanticSearchAsync(queryVec, _topK, wing, room).ConfigureAwait(false))
        {
            if (score >= minScore) dense.Add((drawer, score));
        }
        if (dense.Count > 0)
            return new FallbackResult(dense, FallbackLevel.Dense);

        // Level 2: FTS5 BM25
        var ftsIds = _store.SearchFts(ftsQuery, _topK, wing, room);
        if (ftsIds.Count > 0)
        {
            var ftsDrawers = await BatchLoadDrawersAsync(ftsIds.Select(f => f.DrawerId).ToArray()).ConfigureAwait(false);
            if (ftsDrawers.Count > 0)
                return new FallbackResult(ftsDrawers, FallbackLevel.Fts);
        }

        // Level 3: SQL LIKE fallback
        var like = await SearchLikeAsync(ftsQuery, wing, room).ConfigureAwait(false);
        if (like.Count > 0)
            return new FallbackResult(like.Select(d => (d, 0.5)).ToList(), FallbackLevel.Like);

        return new FallbackResult([], FallbackLevel.None);
    }

    private async Task<List<(PalaceStore.Drawer Drawer, double Score)>> BatchLoadDrawersAsync(string[] drawerIds)
    {
        if (drawerIds.Length == 0) return [];
        var results = new List<(PalaceStore.Drawer, double)>();
        using var conn = new SqliteConnection(_store.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        var placeholders = string.Join(",", drawerIds.Select((_, i) => $"@p{i}"));
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM palace WHERE drawer_id IN ({placeholders}) AND (expires_at IS NULL OR expires_at>@now)";
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        for (int i = 0; i < drawerIds.Length; i++)
            cmd.Parameters.AddWithValue($"@p{i}", drawerIds[i]);

        var idx = 0;
        using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await rdr.ReadAsync().ConfigureAwait(false))
            results.Add((PalaceStore.ReadDrawer(rdr), 1.0 - (idx++ * 0.01)));
        return results;
    }

    private async Task<List<PalaceStore.Drawer>> SearchLikeAsync(string query, string? wing, string? room)
    {
        var result = new List<PalaceStore.Drawer>();
        using var conn = new SqliteConnection(_store.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        var sql = "SELECT * FROM palace WHERE (expires_at IS NULL OR expires_at>@now) AND content LIKE @q";
        if (wing != null) sql += " AND wing=@wing";
        if (room != null) sql += " AND room=@room";
        sql += " ORDER BY importance DESC, created_at DESC LIMIT @k";
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@q", $"%{SanitizeLike(query)}%");
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("@k", _topK);
        if (wing != null) cmd.Parameters.AddWithValue("@wing", wing);
        if (room != null) cmd.Parameters.AddWithValue("@room", room);

        using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await rdr.ReadAsync().ConfigureAwait(false))
            result.Add(PalaceStore.ReadDrawer(rdr));
        return result;
    }

    private static string SanitizeLike(string s)
        => s.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_").Replace("[", "![");
}
