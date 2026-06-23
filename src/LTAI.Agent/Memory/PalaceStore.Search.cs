// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  PalaceStore — semantic + FTS5 + hybrid search
// ═══════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LTAI.Agent.Vector;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

partial class PalaceStore
{
    public async IAsyncEnumerable<(Drawer Drawer, double Score)> SemanticSearchAsync(
        float[] queryVec, int topK = 5, string? wing = null, string? room = null)
    {
        EnsureSchema();
        _ = WarmupHnswAsync();

        if (Interlocked.CompareExchange(ref _hnswReady, 0, 0) == 1 && _hnsw.Count > 0)
        {
            var hnswResults = _hnsw.Search(queryVec, topK * 4);

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

    private static string EscapeFts5(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "\"\"";
        var sanitized = raw
            .Replace("\"", "\"\"")
            .Replace("-", " ")
            .Replace("(", " ")
            .Replace(")", " ")
            .Replace("*", " ")
            .Trim();

        sanitized = Fts5OperatorPattern().Replace(sanitized, " ");
        sanitized = Regex.Replace(sanitized, @"\s+", " ").Trim();

        if (sanitized.Length == 0) return "\"\"";
        return $"\"{sanitized}\"";
    }

    [GeneratedRegex(@"\b(AND|OR|NOT|NEAR)\b", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Fts5OperatorPattern();

    public async Task<IReadOnlyList<(Drawer Drawer, double Score)>> HybridSearchAsync(
        float[] queryVec, string ftsQuery, int topK = 5, string? wing = null, string? room = null,
        string? principal = null, string? scope = null)
    {
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

        var ftsSet = new HashSet<string>(ftsResults.Select(r => r.DrawerId), StringComparer.OrdinalIgnoreCase);
        var semSet = new HashSet<string>(semResults.Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
        const double MajorityVotePenalty = 0.3;

        var topIds = rrf
            .Select(kvp => (id: kvp.Key, score: kvp.Value * (ftsSet.Contains(kvp.Key) && semSet.Contains(kvp.Key) ? 1.0 : MajorityVotePenalty)))
            .OrderByDescending(x => x.score)
            .Take(topK)
            .ToList();

        var resultDrawers = new List<(Drawer Drawer, double Score)>();
        using var batchConn = new SqliteConnection(_connectionString);
        await batchConn.OpenAsync().ConfigureAwait(false);
        var placeholders = string.Join(",", topIds.Select((_, i) => $"@p{i}"));

        // GateMem: scope-based access control at SQL level
        var accessWhere = "(scope='shared'";
        if (principal != null)
            accessWhere += " OR (scope='private' AND principal=@access_principal)";
        if (scope != null)
            accessWhere += " OR scope=@access_scope";
        accessWhere += ")";

        using var batchCmd = batchConn.CreateCommand();
        batchCmd.CommandText = $"SELECT * FROM palace WHERE drawer_id IN ({placeholders}) AND {accessWhere}";
        for (int i = 0; i < topIds.Count; i++)
            batchCmd.Parameters.AddWithValue($"@p{i}", topIds[i].id);
        if (principal != null)
            batchCmd.Parameters.AddWithValue("@access_principal", principal);
        if (scope != null)
            batchCmd.Parameters.AddWithValue("@access_scope", scope);
        using var batchRdr = await batchCmd.ExecuteReaderAsync().ConfigureAwait(false);
        var drawerMap = new Dictionary<string, Drawer>(StringComparer.OrdinalIgnoreCase);
        while (await batchRdr.ReadAsync().ConfigureAwait(false))
        {
            var d = ReadDrawer(batchRdr);
            drawerMap[d.DrawerId] = d;
        }

        foreach (var (id, score) in topIds)
        {
            if (drawerMap.TryGetValue(id, out var drawer))
                resultDrawers.Add((drawer, score));
        }

        if (_searchCache.Count > 128)
        {
            var toRemove = _searchCache.Where(kv => kv.Value.Expiry < DateTime.UtcNow).Take(32).ToList();
            foreach (var kv in toRemove) _searchCache.TryRemove(kv.Key, out _);
        }
        var entry = (DateTime.UtcNow + SearchCacheTtl, (IReadOnlyList<(Drawer, double)>)resultDrawers.AsReadOnly());
        _searchCache[cacheKey] = entry;

        return resultDrawers;
    }

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
}
