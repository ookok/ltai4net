using System;
using Microsoft.Agents.AI; using Microsoft.Extensions.AI; using Microsoft.Extensions.Logging;
using Dapper;
using Npgsql;
using MySqlConnector;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;

namespace LTAI.Agent.Agents;

public sealed class DatabaseAgent : BaseAgent
{
    public DatabaseAgent(AgentCard card, IChatClient brain, SkillRegistry skills, ILogger<DatabaseAgent> logger)
        : base(card, brain, skills, logger) { }

    protected override async Task<AgentResponse> ExecuteLogicAsync(AgentContext context, CancellationToken ct)
    {
        var q = context.UserQuery;
        var connStr = ExtractConnectionString(q);

        if (string.IsNullOrEmpty(connStr))
            return Fail("No connection string found. Include a connection string in the query.");

        var sql = ExtractSql(q);
        if (string.IsNullOrEmpty(sql))
            return Fail("No SQL query found.");

        try
        {
            var provider = DetectProvider(connStr);
            using var conn = provider switch
            {
                "postgres" => new NpgsqlConnection(connStr),
                "mysql" => new MySqlConnection(connStr),
                "sqlite" => new SqliteConnection(connStr),
                "sqlserver" => new SqlConnection(connStr),
                _ => null
            };

            if (conn == null) return Fail("Unsupported database type.");

            await conn.OpenAsync(ct);
            var result = await conn.QueryAsync(sql, cancellationToken: ct);
            var rows = result.Select(r => string.Join(" | ", ((IDictionary<string, object>)r).Values));
            return new AgentResponse(new ChatMessage(ChatRole.Assistant,
                $"Query returned {result.Count()} rows:\n{string.Join("\n", rows)}"));
        }
        catch (Exception ex)
        {
            return Fail($"Database error: {ex.Message}");
        }
    }

    private static string? ExtractConnectionString(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, @"Server=.*?;|Host=.*?;|Data Source=.*?(;|$)|""(.+?)""");
        return m.Success ? m.Value.Trim('"') : null;
    }

    private static string? ExtractSql(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, @"```sql\n([\s\S]*?)```|SELECT.*?;|INSERT.*?;|UPDATE.*?;|DELETE.*?;", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Success ? m.Groups[1].Value : m.Value : null;
    }

    private static string DetectProvider(string connStr)
    {
        if (connStr.Contains("Host=", OrdinalIgnoreCase)) return "postgres";
        if (connStr.Contains("Server=", OrdinalIgnoreCase)) return "sqlserver";
        if (connStr.Contains("DataSource=", OrdinalIgnoreCase)) return "sqlite";
        if (connStr.Contains("mysql", OrdinalIgnoreCase)) return "mysql";
        return "unknown";
    }
}


