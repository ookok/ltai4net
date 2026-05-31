using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using LTAI.AI;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace LTAI.Agent.Tools;

[ToolDomain("data")]
public sealed class DatabaseTools
{
    private static readonly JsonSerializerOptions _indentedJson = new() { WriteIndented = true };

    [Description("执行 SQL 查询并返回 JSON 结果。支持 4 种数据库：\n"
        + "  sqlite     — SQLite 文件数据库\n"
        + "  postgresql — PostgreSQL\n"
        + "  mysql      — MySQL / MariaDB\n"
        + "  sqlserver  — SQL Server\n\n"
        + "支持 SELECT / INSERT / UPDATE / DELETE。写操作需用户确认。\n"
        + "参数:\n"
        + "  provider         — 数据库类型: sqlite / postgresql / mysql / sqlserver\n"
        + "  connectionString — 连接字符串\n"
        + "  sql              — SQL 语句\n"
        + "  parametersJson   — 可选参数 JSON ({\"key\":\"value\"})\n\n"
        + "连接串示例:\n"
        + "  sqlite:     Data Source=mydb.sqlite\n"
        + "  postgresql: Host=localhost;Port=5432;Database=mydb;Username=user;Password=pass\n"
        + "  mysql:      Server=localhost;Database=mydb;User=root;Password=pass\n"
        + "  sqlserver:  Server=localhost;Database=mydb;User Id=sa;Password=pass;TrustServerCertificate=true")]
    public async Task<string> SqlQuery(
        [Description("数据库类型: sqlite / postgresql / mysql / sqlserver")] string provider,
        [Description("连接字符串")] string connectionString,
        [Description("SQL 语句")] string sql,
        [Description("可选参数 (JSON 格式 {\"key\":\"value\"})")] string? parametersJson = null,
        CancellationToken ct = default)
    {
        try
        {
            var trimmed = sql.TrimStart();
            var isWrite = trimmed.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
                       || trimmed.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                       || trimmed.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)
                       || trimmed.StartsWith("DROP", StringComparison.OrdinalIgnoreCase)
                       || trimmed.StartsWith("ALTER", StringComparison.OrdinalIgnoreCase)
                       || trimmed.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase);

            await using var conn = CreateConnection(provider, connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            if (!string.IsNullOrEmpty(parametersJson))
            {
                var parameters = JsonSerializer.Deserialize<Dictionary<string, object?>>(parametersJson);
                if (parameters != null)
                {
                    foreach (var kvp in parameters)
                    {
                        var param = cmd.CreateParameter();
                        param.ParameterName = provider switch
                        {
                            "postgresql" => $":{kvp.Key}",
                            "mysql" => $"?",
                            "sqlserver" => $"@{kvp.Key}",
                            _ => $"${kvp.Key}"
                        };
                        param.Value = kvp.Value ?? DBNull.Value;
                        cmd.Parameters.Add(param);
                    }
                }
            }

            if (isWrite)
            {
                var rows = await cmd.ExecuteNonQueryAsync(ct);
                return $"[SQL] {rows} row(s) affected.\n\nSQL: {sql}";
            }

            var results = new List<Dictionary<string, object?>>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                results.Add(row);
            }

            var json = JsonSerializer.Serialize(results, _indentedJson);
            if (json.Length > 50000)
                json = json[..50000] + $"\n... [truncated at 50000 chars, {results.Count} rows]";

            return $"[SQL] {results.Count} row(s) returned.\n\n{json}";
        }
        catch (OperationCanceledException)
        {
            return "SQL Error: Query cancelled (timeout or user abort)";
        }
        catch (Exception ex)
        {
            return $"SQL Error: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static DbConnection CreateConnection(string provider, string connectionString)
    {
        return provider.ToLowerInvariant() switch
        {
            "postgresql" or "postgres" or "pgsql" => new NpgsqlConnection(connectionString),
            "mysql" or "mariadb" => new MySqlConnection(connectionString),
            "sqlserver" or "mssql" => new SqlConnection(connectionString),
            _ => new SqliteConnection(connectionString),
        };
    }
}
