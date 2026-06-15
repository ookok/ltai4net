using Microsoft.Data.Sqlite;

namespace LTAI.Agent.Memory;

public sealed class SemanticGraph
{
    private readonly Func<SqliteConnection> _factory;
    private readonly double _similarityThreshold;

    public SemanticGraph(Func<SqliteConnection> factory, double similarityThreshold = 0.6)
    {
        _factory = factory;
        _similarityThreshold = similarityThreshold;
    }

    public void InitSchema()
    {
        using var conn = _factory();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS semantic_edges (
                from_id    TEXT NOT NULL,
                to_id      TEXT NOT NULL,
                similarity REAL NOT NULL,
                PRIMARY KEY (from_id, to_id)
            );
            CREATE INDEX IF NOT EXISTS idx_sem_sim ON semantic_edges(similarity DESC);
            """;
        cmd.ExecuteNonQuery();
    }

    public void AddEdge(string fromId, string toId, double similarity)
    {
        if (similarity < _similarityThreshold) return;
        using var conn = _factory();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO semantic_edges (from_id, to_id, similarity)
            VALUES (@from, @to, @sim)
            """;
        cmd.Parameters.AddWithValue("@from", fromId);
        cmd.Parameters.AddWithValue("@to", toId);
        cmd.Parameters.AddWithValue("@sim", similarity);
        cmd.ExecuteNonQuery();
    }

    public List<(string NodeId, double Similarity)> GetNeighbors(string nodeId, int topK = 20)
    {
        using var conn = _factory();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT to_id, similarity FROM semantic_edges
            WHERE from_id = @id AND similarity >= @threshold
            UNION ALL
            SELECT from_id, similarity FROM semantic_edges
            WHERE to_id = @id AND similarity >= @threshold
            ORDER BY similarity DESC LIMIT @topK
            """;
        cmd.Parameters.AddWithValue("@id", nodeId);
        cmd.Parameters.AddWithValue("@threshold", _similarityThreshold);
        cmd.Parameters.AddWithValue("@topK", topK);
        var results = new List<(string, double)>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
            results.Add((rdr.GetString(0), rdr.GetDouble(1)));
        return results;
    }
}
