using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace LTAI.Core.Configuration;

/// <summary>
/// P0: SQLite-backed persistence for the LLM provider circuit breaker state.
/// Stores consecutive failure counts and cooldown end timestamps so that a
/// process restart does not reset the breaker — providers that were failing
/// before the restart stay in cooldown until their expiry passes.
///
/// Schema:
///   circuit_breaker(provider TEXT PRIMARY KEY, failures INT, cooldown_until TEXT)
///
/// WAL mode enabled for concurrent-read performance (single writer via lock).
/// No cache — every read/write hits SQLite directly (low-frequency path).
/// </summary>
public sealed class CircuitBreakerStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    private const string CreateTable = """
        CREATE TABLE IF NOT EXISTS circuit_breaker (
            provider        TEXT PRIMARY KEY,
            failures        INTEGER NOT NULL DEFAULT 0,
            cooldown_until  TEXT
        )
    """;

    private const string UpsertSql = """
        INSERT INTO circuit_breaker (provider, failures, cooldown_until)
        VALUES (@provider, @failures, @cooldown_until)
        ON CONFLICT(provider) DO UPDATE SET
            failures = @failures, cooldown_until = @cooldown_until
    """;

    public CircuitBreakerStore(string dbPath)
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode = WAL";
        cmd.ExecuteNonQuery();
        cmd.CommandText = CreateTable;
        cmd.ExecuteNonQuery();
    }

    public async Task SaveAsync(string provider, int failures, DateTime? cooldownUntil)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = UpsertSql;
            cmd.Parameters.AddWithValue("@provider", provider);
            cmd.Parameters.AddWithValue("@failures", failures);
            cmd.Parameters.AddWithValue("@cooldown_until",
                cooldownUntil?.ToString("O") ?? (object)DBNull.Value);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    public async Task<(int failures, DateTime? cooldownUntil)> LoadAsync(string provider)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT failures, cooldown_until FROM circuit_breaker WHERE provider = @provider";
        cmd.Parameters.AddWithValue("@provider", provider);
        using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        if (await reader.ReadAsync().ConfigureAwait(false))
        {
            var failures = reader.GetInt32(0);
            DateTime? cooldown = reader.IsDBNull(1) ? null : DateTime.Parse(reader.GetString(1),
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
            return (failures, cooldown);
        }
        return (0, null);
    }

    public async Task<Dictionary<string, (int failures, DateTime? cooldownUntil)>> LoadAllAsync()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT provider, failures, cooldown_until FROM circuit_breaker";
        using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        var result = new Dictionary<string, (int, DateTime?)>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var provider = reader.GetString(0);
            var failures = reader.GetInt32(1);
            DateTime? cooldown = reader.IsDBNull(2) ? null : DateTime.Parse(reader.GetString(2),
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
            result[provider] = (failures, cooldown);
        }
        return result;
    }

    public async Task ClearAsync(string provider)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM circuit_breaker WHERE provider = @provider";
            cmd.Parameters.AddWithValue("@provider", provider);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _conn.Close();
        _conn.Dispose();
        _lock.Dispose();
    }
}
