using Microsoft.Data.Sqlite;

namespace LTAI.Agent.Memory;

public sealed class CausalGraph
{
    private readonly Func<SqliteConnection> _factory;
    private readonly double _threshold;

    public CausalGraph(Func<SqliteConnection> factory, double threshold = 0.7)
    {
        _factory = factory;
        _threshold = threshold;
    }

    public void InitSchema()
    {
        using var conn = _factory();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS causal_edges (
                from_id  TEXT NOT NULL,
                to_id    TEXT NOT NULL,
                score    REAL NOT NULL DEFAULT 0.0,
                llm_label TEXT,
                PRIMARY KEY (from_id, to_id)
            );
            CREATE INDEX IF NOT EXISTS idx_causal_score ON causal_edges(score DESC);
            """;
        cmd.ExecuteNonQuery();
    }

    public void AddEdge(string fromId, string toId, double score, string? label = null)
    {
        if (score < _threshold) return;
        using var conn = _factory();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO causal_edges (from_id, to_id, score, llm_label)
            VALUES (@from, @to, @score, @label)
            """;
        cmd.Parameters.AddWithValue("@from", fromId);
        cmd.Parameters.AddWithValue("@to", toId);
        cmd.Parameters.AddWithValue("@score", score);
        cmd.Parameters.AddWithValue("@label", label ?? "");
        cmd.ExecuteNonQuery();
    }

    public List<(string NodeId, double Score, string? Label)> GetCauses(string nodeId, int topK = 10)
    {
        using var conn = _factory();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT from_id, score, llm_label FROM causal_edges
            WHERE to_id = @nodeId AND score >= @threshold
            ORDER BY score DESC LIMIT @topK
            """;
        cmd.Parameters.AddWithValue("@nodeId", nodeId);
        cmd.Parameters.AddWithValue("@threshold", _threshold);
        cmd.Parameters.AddWithValue("@topK", topK);
        var results = new List<(string, double, string?)>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
            results.Add((rdr.GetString(0), rdr.GetDouble(1), rdr.IsDBNull(2) ? null : rdr.GetString(2)));
        return results;
    }

    public List<(string NodeId, double Score, string? Label)> GetEffects(string nodeId, int topK = 10)
    {
        using var conn = _factory();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT to_id, score, llm_label FROM causal_edges
            WHERE from_id = @nodeId AND score >= @threshold
            ORDER BY score DESC LIMIT @topK
            """;
        cmd.Parameters.AddWithValue("@nodeId", nodeId);
        cmd.Parameters.AddWithValue("@threshold", _threshold);
        cmd.Parameters.AddWithValue("@topK", topK);
        var results = new List<(string, double, string?)>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
            results.Add((rdr.GetString(0), rdr.GetDouble(1), rdr.IsDBNull(2) ? null : rdr.GetString(2)));
        return results;
    }

    public int PendingCount()
    {
        using var conn = _factory();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM consolidation_queue WHERE status = 'pending'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
