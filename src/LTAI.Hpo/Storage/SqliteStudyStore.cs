using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace LTAI.Hpo.Storage;

/// <summary>
/// SQLite-backed persistent store for study trials.
/// Each study gets its own table: <c>hpo_{studyName}</c>.
/// </summary>
public sealed class SqliteStudyStore : IStudyStore
{
    private readonly string _connectionString;
    private readonly Dictionary<string, string> _tableNames = new();

    public SqliteStudyStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task InitializeAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode = WAL;";
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task SaveTrialAsync(string studyName, TrialRecord record)
    {
        var table = GetTableName(studyName);
        await EnsureTableAsync(studyName, table).ConfigureAwait(false);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            INSERT OR REPLACE INTO [{table}]
                (number, state, value, params_json, intermediates_json, error_message, created_at)
            VALUES ($num, $state, $val, $params, $inter, $err, $ts)";
        cmd.Parameters.AddWithValue("$num", record.Number);
        cmd.Parameters.AddWithValue("$state", (int)record.State);
        cmd.Parameters.AddWithValue("$val", (object?)record.Value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$params", System.Text.Json.JsonSerializer.Serialize(record.Params));
        cmd.Parameters.AddWithValue("$inter", System.Text.Json.JsonSerializer.Serialize(record.IntermediateValues));
        cmd.Parameters.AddWithValue("$err", (object?)record.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ts", record.CreatedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TrialRecord>> LoadTrialsAsync(string studyName)
    {
        var table = GetTableName(studyName);
        await EnsureTableAsync(studyName, table).ConfigureAwait(false);

        var results = new List<TrialRecord>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM [{table}] ORDER BY number";
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            results.Add(new TrialRecord
            {
                Number = reader.GetInt32(0),
                State = (TrialState)reader.GetInt32(1),
                Value = reader.IsDBNull(2) ? null : reader.GetDouble(2),
                Params = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(
                    reader.GetString(3)) ?? new(),
                IntermediateValues = System.Text.Json.JsonSerializer.Deserialize<List<TrialValue>>(
                    reader.GetString(4)) ?? new(),
                ErrorMessage = reader.IsDBNull(5) ? null : reader.GetString(5),
                CreatedAt = DateTime.Parse(reader.GetString(6)),
            });
        }
        return results;
    }

    // ── helpers ──

    private string GetTableName(string studyName)
    {
        var sanitized = studyName.Replace("\"", "\"\"").Replace("'", "''");
        return $"hpo_{sanitized}";
    }

    private async Task EnsureTableAsync(string studyName, string table)
    {
        if (_tableNames.ContainsKey(studyName)) return;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            CREATE TABLE IF NOT EXISTS [{table}] (
                number             INTEGER PRIMARY KEY,
                state              INTEGER NOT NULL,
                value              REAL,
                params_json        TEXT NOT NULL,
                intermediates_json TEXT NOT NULL DEFAULT '[]',
                error_message      TEXT,
                created_at         TEXT NOT NULL
            )";
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

        _tableNames[studyName] = table;
    }
}