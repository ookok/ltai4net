using Microsoft.Data.Sqlite;

namespace LTAI.Agent.Memory;

/// <summary>
/// EvoEmbedding-inspired temporal graph with time-decay weighted retrieval.
/// Provides evolvable (time-aware) chain traversal — recent edges weighted
/// higher via exponential decay. Prevents context staleness in long conversations.
/// </summary>
public sealed class TemporalGraph
{
    private readonly Func<SqliteConnection> _factory;

    public TemporalGraph(Func<SqliteConnection> factory)
    {
        _factory = factory;
    }

    public void InitSchema()
    {
        using var conn = _factory();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS temporal_edges (
                from_id TEXT NOT NULL,
                to_id   TEXT NOT NULL,
                seq     INTEGER NOT NULL,
                created_at INTEGER NOT NULL DEFAULT (unixepoch('subsec') * 1000),
                PRIMARY KEY (from_id, to_id)
            );
            CREATE INDEX IF NOT EXISTS idx_temp_seq ON temporal_edges(seq);
            CREATE INDEX IF NOT EXISTS idx_temp_created ON temporal_edges(created_at);
            """;
        cmd.ExecuteNonQuery();
        // Migration: add created_at if missing
        try
        {
            using var migCmd = conn.CreateCommand();
            migCmd.CommandText = "ALTER TABLE temporal_edges ADD COLUMN created_at INTEGER NOT NULL DEFAULT (unixepoch('subsec') * 1000)";
            migCmd.ExecuteNonQuery();
        }
        catch { }
    }

    public void Append(string nodeId, int seq)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var conn = _factory();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO temporal_edges (from_id, to_id, seq, created_at)
            SELECT COALESCE(
                (SELECT to_id FROM temporal_edges ORDER BY seq DESC LIMIT 1),
                @nodeId
            ), @nodeId, @seq, @ts
            """;
        cmd.Parameters.AddWithValue("@nodeId", nodeId);
        cmd.Parameters.AddWithValue("@seq", seq);
        cmd.Parameters.AddWithValue("@ts", now);
        cmd.ExecuteNonQuery();
    }

    public List<string> GetChain(int maxSteps = 50)
    {
        using var conn = _factory();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH RECURSIVE chain AS (
                SELECT from_id, to_id, seq, 0 AS depth
                FROM temporal_edges
                WHERE seq = (SELECT MIN(seq) FROM temporal_edges)
                UNION ALL
                SELECT e.from_id, e.to_id, e.seq, c.depth + 1
                FROM temporal_edges e
                INNER JOIN chain c ON c.to_id = e.from_id
                WHERE c.depth < @max
            )
            SELECT to_id FROM chain ORDER BY seq LIMIT @max
            """;
        cmd.Parameters.AddWithValue("@max", maxSteps);
        var results = new List<string>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read()) results.Add(rdr.GetString(0));
        return results;
    }

    /// <summary>
    /// EvoEmbedding-inspired time-decay weighted chain retrieval.
    /// Recent edges are weighted higher via exponential decay: weight = exp(-age / halfLife).
    /// </summary>
    /// <param name="maxSteps">Maximum steps to traverse.</param>
    /// <param name="halfLifeMs">Half-life for time decay in milliseconds (default: 5 minutes).</param>
    /// <returns>(nodeId, decayWeight) pairs ordered by seq.</returns>
    public List<(string NodeId, double DecayWeight)> GetChainWithDecay(
        int maxSteps = 50, double halfLifeMs = 300_000)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var conn = _factory();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH RECURSIVE chain AS (
                SELECT from_id, to_id, seq, created_at, 0 AS depth
                FROM temporal_edges
                WHERE seq = (SELECT MIN(seq) FROM temporal_edges)
                UNION ALL
                SELECT e.from_id, e.to_id, e.seq, e.created_at, c.depth + 1
                FROM temporal_edges e
                INNER JOIN chain c ON c.to_id = e.from_id
                WHERE c.depth < @max
            )
            SELECT to_id, created_at FROM chain ORDER BY seq LIMIT @max
            """;
        cmd.Parameters.AddWithValue("@max", maxSteps);
        var results = new List<(string, double)>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            var nodeId = rdr.GetString(0);
            var created = rdr.GetInt64(1);
            var age = Math.Max(0, now - created);
            var weight = Math.Exp(-age / halfLifeMs);
            results.Add((nodeId, weight));
        }
        return results;
    }

    /// <summary>
    /// Get recent edges within a time window. Fast pruning for long conversations.
    /// </summary>
    /// <param name="maxSteps">Maximum steps to traverse.</param>
    /// <param name="windowMs">Time window in milliseconds (default: 10 minutes).</param>
    public List<string> GetRecentChain(int maxSteps = 50, double windowMs = 600_000)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)windowMs;
        using var conn = _factory();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH RECURSIVE chain AS (
                SELECT from_id, to_id, seq, created_at, 0 AS depth
                FROM temporal_edges
                WHERE seq = (SELECT MIN(seq) FROM temporal_edges WHERE created_at >= @cutoff)
                  AND created_at >= @cutoff
                UNION ALL
                SELECT e.from_id, e.to_id, e.seq, e.created_at, c.depth + 1
                FROM temporal_edges e
                INNER JOIN chain c ON c.to_id = e.from_id
                WHERE c.depth < @max AND e.created_at >= @cutoff
            )
            SELECT to_id FROM chain ORDER BY seq LIMIT @max
            """;
        cmd.Parameters.AddWithValue("@max", maxSteps);
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        var results = new List<string>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read()) results.Add(rdr.GetString(0));
        return results;
    }

    /// <summary>
    /// Get the average age of edges in the chain (diagnostic).
    /// </summary>
    public double GetAverageAgeMs()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var conn = _factory();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT AVG(@now - created_at) FROM temporal_edges";
        cmd.Parameters.AddWithValue("@now", now);
        var result = cmd.ExecuteScalar();
        return result is double avg ? avg : 0;
    }
}
