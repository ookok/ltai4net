using Microsoft.Data.Sqlite;

namespace LTAI.Agent.Memory;

public sealed class TemporalGraph
{
    private readonly SqliteConnection _db;

    public TemporalGraph(SqliteConnection db)
    {
        _db = db;
    }

    public void InitSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS temporal_edges (
                from_id TEXT NOT NULL,
                to_id   TEXT NOT NULL,
                seq     INTEGER NOT NULL,
                PRIMARY KEY (from_id, to_id)
            );
            CREATE INDEX IF NOT EXISTS idx_temp_seq ON temporal_edges(seq);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Append(string nodeId, int seq)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO temporal_edges (from_id, to_id, seq)
            SELECT COALESCE(
                (SELECT to_id FROM temporal_edges ORDER BY seq DESC LIMIT 1),
                @nodeId
            ), @nodeId, @seq
            """;
        cmd.Parameters.AddWithValue("@nodeId", nodeId);
        cmd.Parameters.AddWithValue("@seq", seq);
        cmd.ExecuteNonQuery();
    }

    public List<string> GetChain(int maxSteps = 50)
    {
        using var cmd = _db.CreateCommand();
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
}
