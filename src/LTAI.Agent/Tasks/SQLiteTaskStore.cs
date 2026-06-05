using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LTAI.Agent.Tasks;

public sealed class SQLiteTaskStore : ITaskStore, IAsyncDisposable
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS task_queue (
            id          TEXT PRIMARY KEY,
            name        TEXT NOT NULL,
            description TEXT,
            enqueued_at TEXT NOT NULL,
            started_at  TEXT,
            completed_at TEXT,
            status      INTEGER NOT NULL DEFAULT 0,
            result      TEXT,
            error       TEXT,
            attempt     INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS idx_task_status ON task_queue(status);
        """;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _schemaReady;

    public SQLiteTaskStore(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public async Task SaveAsync(TaskItem item, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureSchemaAsync().ConfigureAwait(false);
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO task_queue(id, name, description, enqueued_at,
                    started_at, completed_at, status, result, error, attempt)
                VALUES($id, $name, $desc, $enq, $start, $comp, $status, $res, $err, $attempt)
                """;
            cmd.Parameters.AddWithValue("$id", item.Id);
            cmd.Parameters.AddWithValue("$name", item.Name);
            cmd.Parameters.AddWithValue("$desc", (object?)item.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$enq", item.EnqueuedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$start", (object?)item.StartedAt?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$comp", (object?)item.CompletedAt?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$status", (int)item.Status);
            cmd.Parameters.AddWithValue("$res", (object?)item.Result ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$err", (object?)item.Error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$attempt", item.Attempt);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public Task UpdateAsync(TaskItem item, CancellationToken ct = default)
        => SaveAsync(item, ct);

    public async Task<IReadOnlyList<TaskItem>> LoadPendingAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync().ConfigureAwait(false);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM task_queue WHERE status IN (0, 1) ORDER BY enqueued_at";
        var items = new List<TaskItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            items.Add(ReadItem(reader));
        return items;
    }

    public async Task<IReadOnlyList<TaskItem>> LoadAllAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync().ConfigureAwait(false);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM task_queue ORDER BY enqueued_at DESC LIMIT 100";
        var items = new List<TaskItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            items.Add(ReadItem(reader));
        return items;
    }

    public async Task CleanupAsync(int keepCount = 50, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureSchemaAsync().ConfigureAwait(false);
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM task_queue WHERE id NOT IN (
                    SELECT id FROM task_queue ORDER BY enqueued_at DESC LIMIT $keep
                )
                """;
            cmd.Parameters.AddWithValue("$keep", keepCount);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureSchemaAsync()
    {
        if (_schemaReady) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_schemaReady) return;
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync().ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode = WAL; " + Schema;
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            _schemaReady = true;
        }
        finally { _gate.Release(); }
    }

    private static TaskItem ReadItem(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Name = r.GetString(1),
        Description = r.IsDBNull(2) ? null : r.GetString(2),
        EnqueuedAt = DateTimeOffset.Parse(r.GetString(3)),
        StartedAt = r.IsDBNull(4) ? null : DateTimeOffset.Parse(r.GetString(4)),
        CompletedAt = r.IsDBNull(5) ? null : DateTimeOffset.Parse(r.GetString(5)),
        Status = (TaskStatus)r.GetInt32(6),
        Result = r.IsDBNull(7) ? null : r.GetString(7),
        Error = r.IsDBNull(8) ? null : r.GetString(8),
        Attempt = r.GetInt32(9),
    };

    public async ValueTask DisposeAsync()
    {
        _gate.Dispose();
    }
}
