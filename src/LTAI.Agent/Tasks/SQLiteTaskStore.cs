// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  SQLiteTaskStore — SQLite-backed task queue (in kg.db)
//
//  OPTIMIZED: connects to a shared kg.db instead of a standalone db.
//  Uses table name "task_queue" to avoid collision with KgStore tables.
// ═══════════════════════════════════════════════════════════════

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

    /// <summary>Connect to any SQLite database (including shared kg.db).</summary>
    public SQLiteTaskStore(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    /// <summary>Factory: share KgStore's kg.db.</summary>
    public static SQLiteTaskStore CreateShared(string kgDbPath) => new(kgDbPath);

    // ═══════════════════════════════════════════
    //  ITaskStore
    // ═══════════════════════════════════════════

    public async Task SaveAsync(TaskItem item, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO task_queue (id,name,description,enqueued_at,status,attempt) VALUES ($id,$name,$desc,$enq,0,$attempt)";
        cmd.Parameters.AddWithValue("$id", item.Id);
        cmd.Parameters.AddWithValue("$name", item.Name ?? "");
        cmd.Parameters.AddWithValue("$desc", item.Description ?? "");
        cmd.Parameters.AddWithValue("$enq", item.EnqueuedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$attempt", item.Attempt);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(TaskItem item, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE task_queue SET status=$status,attempt=$attempt WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", item.Id);
        cmd.Parameters.AddWithValue("$status", (int)item.Status);
        cmd.Parameters.AddWithValue("$attempt", item.Attempt);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TaskItem>> LoadPendingAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id,name,description,enqueued_at,attempt,status FROM task_queue WHERE status=0 ORDER BY enqueued_at ASC";
        using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var items = new List<TaskItem>();
        while (rdr.Read())
            items.Add(CreateItem(rdr));
        return items;
    }

    // ═══════════════════════════════════════════
    //  Fine-grained
    // ═══════════════════════════════════════════

    public async Task EnqueueAsync(TaskItem item, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO task_queue (id,name,description,enqueued_at,status) VALUES ($id,$name,$desc,$now,0)";
        cmd.Parameters.AddWithValue("$id", item.Id);
        cmd.Parameters.AddWithValue("$name", item.Name ?? "");
        cmd.Parameters.AddWithValue("$desc", item.Description ?? "");
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<TaskItem?> DequeueAsync()
    {
        await EnsureSchemaAsync().ConfigureAwait(false);
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id,name,description,enqueued_at,attempt,status FROM task_queue WHERE status=0 ORDER BY enqueued_at ASC LIMIT 1";
        using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        return rdr.Read() ? CreateItem(rdr) : null;
    }

    public async Task MarkStartedAsync(string id, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE task_queue SET status=1,started_at=$now WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkCompletedAsync(string id, string? result = null, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE task_queue SET status=2,completed_at=$now,result=$result WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$result", result ?? "");
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkFailedAsync(string id, string error, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE task_queue SET status=3,error=$error,attempt=attempt+1 WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$error", error);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TaskItem>> ListAllAsync()
    {
        await EnsureSchemaAsync().ConfigureAwait(false);
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id,name,description,enqueued_at,attempt,status FROM task_queue ORDER BY enqueued_at DESC LIMIT 100";
        using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        var items = new List<TaskItem>();
        while (rdr.Read()) items.Add(CreateItem(rdr));
        return items;
    }

    public async Task<int> CleanupOldAsync(TimeSpan maxAge, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM task_queue WHERE completed_at IS NOT NULL AND completed_at < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", DateTime.UtcNow.Subtract(maxAge).ToString("O"));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static TaskItem CreateItem(SqliteDataReader r) => new()
    {
        Id = r.GetString(0), Name = r.GetString(1),
        Description = r.IsDBNull(2) ? null : r.GetString(2),
        EnqueuedAt = DateTime.Parse(r.GetString(3), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
        Attempt = r.GetInt32(4),
    };

    private async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        if (_schemaReady) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_schemaReady) return;
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = Schema;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _schemaReady = true;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _gate.Dispose();
        await Task.CompletedTask;
    }
}
