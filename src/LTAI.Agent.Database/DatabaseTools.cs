using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using LTAI.AI;
using LTAI.Core.Configuration;
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
        + "支持 SELECT / INSERT / UPDATE / DELETE。写操作由 MAF ToolApprovalAgent 审批。\n"
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
            // ── SSRF protection: only allow local connections for network databases ──
            var providerLower = provider.ToLowerInvariant();
            if (providerLower is "postgresql" or "postgres" or "pgsql" or "mysql" or "mariadb" or "sqlserver" or "mssql")
            {
                var host = ExtractHost(connectionString);
                if (host != null && !IsLocalConnection(host))
                    return "[SQL] 只允许连接到本地数据库服务器。远程连接已禁用。";
            }

            // ── Multi-statement injection detection ──
            var trimmed = sql.TrimStart();
            var semicolonIdx = trimmed.IndexOf(';');
            if (semicolonIdx >= 0)
            {
                // Allow trailing semicolon only (single statement)
                var afterSemicolon = trimmed[(semicolonIdx + 1)..].Trim();
                if (afterSemicolon.Length > 0)
                    return "[SQL] 不支持多条 SQL 语句。一次只允许执行一条语句。";
            }

            var isWrite = trimmed.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
                       || trimmed.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                       || trimmed.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)
                       || trimmed.StartsWith("DROP", StringComparison.OrdinalIgnoreCase)
                       || trimmed.StartsWith("ALTER", StringComparison.OrdinalIgnoreCase)
                       || trimmed.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase);

            if (!isWrite && !trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && !trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)
                && !trimmed.StartsWith("EXPLAIN", StringComparison.OrdinalIgnoreCase))
                return "[SQL] 只支持 SELECT / WITH / EXPLAIN 查询语句。写操作需要审批。PRAGMA 已禁用。";

            await using var conn = CreateConnection(providerLower, connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
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
                var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return $"[SQL] {rows} row(s) affected.\n\nSQL: {sql}";
            }

            var results = new List<Dictionary<string, object?>>();
            const int maxRows = 5000;
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (results.Count >= maxRows)
                {
                    await reader.CloseAsync().ConfigureAwait(false);
                    break;
                }
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                results.Add(row);
            }

            var truncated = results.Count >= maxRows;
            var json = JsonSerializer.Serialize(results, _indentedJson);
            if (json.Length > 50000)
                json = ContentTruncator.Truncate(json, 50000);

            var note = truncated ? $" (truncated at {maxRows} rows)" : "";
            return $"[SQL] {results.Count} row(s) returned{note}.\n\n{json}";
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

    private static string? ExtractHost(string connStr)
    {
        // Connection string parsers (Npgsql, MySqlConnector, SqlClient) use LAST occurrence.
        string? result = null;
        foreach (var keyword in new[] { "Host=", "Server=", "Data Source=", "DataSource=" })
        {
            var idx = connStr.LastIndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var start = idx + keyword.Length;
            var end = connStr.IndexOfAny([';', ' ', ','], start);
            var val = end > start ? connStr[start..end] : connStr[start..];
            result = val.Trim('\'', '"');
        }
        return result;
    }

    private static bool IsLocalConnection(string host)
    {
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "127.0.0.1")
            || string.Equals(host, "::1")
            || string.Equals(host, "[::1]")
            || string.Equals(host, "0:0:0:0:0:0:0:1")
            || string.Equals(host, ".")
            || string.Equals(host, "(local)")
            || host.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "host.docker.internal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "gateway.docker.internal", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".svc.cluster.local", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("np:", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("lpc:", StringComparison.OrdinalIgnoreCase);
    }
}
