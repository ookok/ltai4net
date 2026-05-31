using System.ComponentModel;
using System.Data;
using LTAI.AI;
using Microsoft.Data.Sqlite;

namespace LTAI.Agent.Tools;

[ToolDomain("data")]
public sealed class DatabaseTools
{
    [Description("执行 SQL 查询并返回 JSON 结果。支持 SELECT、INSERT、UPDATE、DELETE。\n"
        + "适用场景：查询数据库、更新数据、验证数据完整性。\n"
        + "警告：INSERT/UPDATE/DELETE 等写操作需要用户确认。\n"
        + "关键参数：connectionString — SQLite 连接字符串；sql — SQL 语句；parametersJson — 可选参数 JSON（{\"key\":\"value\"}）。")]
    public string SqlQuery(string connectionString, string sql, string? parametersJson = null)
    {
        try
        {
            var isWrite = sql.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
                       || sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                       || sql.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)
                       || sql.TrimStart().StartsWith("DROP", StringComparison.OrdinalIgnoreCase)
                       || sql.TrimStart().StartsWith("ALTER", StringComparison.OrdinalIgnoreCase)
                       || sql.TrimStart().StartsWith("CREATE", StringComparison.OrdinalIgnoreCase);

            using var conn = new SqliteConnection(connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            if (!string.IsNullOrEmpty(parametersJson))
            {
                var parameters = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(parametersJson);
                if (parameters != null)
                {
                    foreach (var kvp in parameters)
                    {
                        cmd.Parameters.AddWithValue($"${kvp.Key}", kvp.Value ?? DBNull.Value);
                    }
                }
            }

            if (isWrite)
            {
                var rows = cmd.ExecuteNonQuery();
                return $"[SQL] {rows} row(s) affected.\n\nSQL: {sql}";
            }

            var results = new List<Dictionary<string, object?>>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                results.Add(row);
            }

            var json = System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            if (json.Length > 50000)
                json = json[..50000] + $"\n... [truncated at 50000 chars, {results.Count} rows]";

            return $"[SQL] {results.Count} row(s) returned.\n\n{json}";
        }
        catch (Exception ex)
        {
            return $"SQL Error: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
