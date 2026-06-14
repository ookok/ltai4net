using Microsoft.Data.Sqlite;

namespace LTAI.Agent.Memory;

public sealed class EntityGraph
{
    private readonly SqliteConnection _db;

    public EntityGraph(SqliteConnection db)
    {
        _db = db;
    }

    public void InitSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS entity_edges (
                node_id     TEXT NOT NULL,
                entity_id   TEXT NOT NULL,
                entity_type TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (node_id, entity_id)
            );
            CREATE INDEX IF NOT EXISTS idx_entity_eid ON entity_edges(entity_id);
            CREATE INDEX IF NOT EXISTS idx_entity_type ON entity_edges(entity_type);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Link(string nodeId, string entityId, string entityType = "")
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO entity_edges (node_id, entity_id, entity_type)
            VALUES (@node, @entity, @type)
            """;
        cmd.Parameters.AddWithValue("@node", nodeId);
        cmd.Parameters.AddWithValue("@entity", entityId);
        cmd.Parameters.AddWithValue("@type", string.IsNullOrEmpty(entityType) ? "default" : entityType);
        cmd.ExecuteNonQuery();
    }

    public List<string> GetNodesForEntity(string entityId, int limit = 20)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT node_id FROM entity_edges
            WHERE entity_id = @entity
            ORDER BY rowid DESC LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@entity", entityId);
        cmd.Parameters.AddWithValue("@limit", limit);
        var results = new List<string>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read()) results.Add(rdr.GetString(0));
        return results;
    }

    public List<(string EntityId, string EntityType)> GetEntities(string nodeId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT entity_id, entity_type FROM entity_edges
            WHERE node_id = @node
            """;
        cmd.Parameters.AddWithValue("@node", nodeId);
        var results = new List<(string, string)>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
            results.Add((rdr.GetString(0), rdr.GetString(1)));
        return results;
    }
}
