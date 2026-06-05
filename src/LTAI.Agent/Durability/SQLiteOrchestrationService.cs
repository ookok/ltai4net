// Copyright (c) LTAI. All rights reserved.

using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using DurableTask.Core;
using DurableTask.Core.Query;
using Microsoft.Data.Sqlite;
using Microsoft.DurableTask.Testing.Sidecar;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Durability;

/// <summary>
/// SQLite-backed orchestration service for the MAF Durable Task pipeline (P8.1).
///
/// Why: <c>Microsoft.DurableTask.InProcessTestHost</c> ships an in-process
/// <see cref="InMemoryOrchestrationService"/> (process-lifetime state). Restart the
/// process, lose every pending orchestration + entity. This class is a drop-in
/// replacement: it inherits all four DTFx service interfaces, layers SQLite
/// snapshotting of the instance store on top, and rehydrates the inner
/// <see cref="InMemoryOrchestrationService"/> on startup so a cold-start can
/// resume in-flight work.
///
/// Architecture:
///   1. Subclass of <see cref="InMemoryOrchestrationService"/> via the
///      <c>new</c> keyword on every mutating method (the base class methods are
///      not <c>virtual</c>). The base's stored <c>instanceStore</c> private
///      field is reached via reflection.
///   2. The instance store is a private nested class
///      (<c>InMemoryInstanceStore</c>) with one private field
///      (<c>store: ConcurrentDictionary&lt;string, SerializedInstanceState&gt;</c>).
///      We reach it through reflection and serialize each
///      <c>SerializedInstanceState</c> via its already-in-memory
///      <c>JsonValue</c> / <c>JsonArray</c> payloads (the in-memory service uses
///      <c>System.Text.Json.Nodes</c> as its in-memory JSON cache, so we get
///      free round-trip).
///   3. Every write that mutates the instance store calls our
///      <c>PersistAllAsync</c> snapshotter, which upserts a row per
///      <c>SerializedInstanceState</c> in a single SQLite transaction.
///   4. On startup, <c>StartAsync</c> reads the rows and repopulates the inner
///      dictionary + readyToRunQueue via reflection (process-local lock state
///      stays unlocked — that is intentional and correct).
///
/// State scope: durable across process restarts. Lock state and channel waiters
/// are process-local and start fresh — DTFx workflows resume naturally because
/// the in-memory <c>readyToRunQueue</c> is repopulated from the audit log.
///
/// Source references (DTFx v1.24.2, extern/durabletask-dotnet submodule):
///   Base class: <see cref="InMemoryOrchestrationService"/>
///     → src/InProcessTestHost/Sidecar/InMemoryOrchestrationService.cs
///   gRPC sidecar (TaskHubGrpcServer):
///     → src/InProcessTestHost/Sidecar/Grpc/TaskHubGrpcServer.cs
///   AddInMemoryDurableTask extension:
///     → src/InProcessTestHost/DurableTaskTestExtensions.cs
///
/// Concurrency: SQLite is single-writer; we serialize all writes behind a
/// <see cref="SemaphoreSlim"/>. Reads (Hydrate) are read-only and use a separate
/// connection.
///
/// Why <c>new</c> not <c>override</c>: <c>InMemoryOrchestrationService</c> does
/// not mark its interface methods <c>virtual</c>, so the only way to interpose
/// is method hiding. This is safe here because the only direct consumer of the
/// concrete type is <c>InMemoryGrpcSidecarHost</c>, which merely stores the
/// reference and re-registers it through the <c>IOrchestrationService</c> /
/// <c>IOrchestrationServiceClient</c> interfaces — it never calls any method on
/// the base reference.
/// </summary>
public sealed class SQLiteOrchestrationService : InMemoryOrchestrationService
{
    // Source: src/InProcessTestHost/Sidecar/InMemoryOrchestrationService.cs (private field)
    static readonly FieldInfo s_instanceStoreField =
        typeof(InMemoryOrchestrationService).GetField("instanceStore", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("Could not reflect InMemoryOrchestrationService.instanceStore");

    // Source: nested InMemoryInstanceStore (same file), private field
    static readonly FieldInfo s_innerStoreField = s_instanceStoreField.FieldType
        .GetField("store", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("Could not reflect InMemoryInstanceStore.store");

    // Source: inner type SerializedInstanceState (same file, same nested class)
    static readonly Type s_serializedInstanceStateType = s_innerStoreField.FieldType.GetGenericArguments()[1];

    // Source: SerializedInstanceState public fields (JSON round-trip via System.Text.Json.Nodes)
    static readonly FieldInfo s_statusRecordField =
        s_serializedInstanceStateType.GetField("StatusRecordJson", BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException("Could not reflect SerializedInstanceState.StatusRecordJson");

    static readonly FieldInfo s_historyField =
        s_serializedInstanceStateType.GetField("HistoryEventsJson", BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException("Could not reflect SerializedInstanceState.HistoryEventsJson");

    static readonly FieldInfo s_messagesField =
        s_serializedInstanceStateType.GetField("MessagesJson", BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException("Could not reflect SerializedInstanceState.MessagesJson");

    static readonly FieldInfo s_executionIdField =
        s_serializedInstanceStateType.GetField("ExecutionId", BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException("Could not reflect SerializedInstanceState.ExecutionId");

    static readonly FieldInfo s_isCompletedField =
        s_serializedInstanceStateType.GetField("IsCompleted", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("Could not reflect SerializedInstanceState.IsCompleted");

    // Source: nested InMemoryInstanceStore (same file), private field
    static readonly FieldInfo s_readyToRunQueueField = s_instanceStoreField.FieldType
        .GetField("readyToRunQueue", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("Could not reflect InMemoryInstanceStore.readyToRunQueue");

    // Source: s_readyToRunQueueField.FieldType (ReadyToRunQueue class, same file)
    static readonly MethodInfo s_scheduleMethod = s_readyToRunQueueField.FieldType
        .GetMethod("Schedule", BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException("Could not reflect ReadyToRunQueue.Schedule");

    // Source: SerializedInstanceState constructor (same file)
    static readonly ConstructorInfo s_serializedInstanceStateCtor = s_serializedInstanceStateType
        .GetConstructor(new[] { typeof(string), typeof(string) })
        ?? throw new InvalidOperationException("Could not reflect SerializedInstanceState ctor");

    readonly string _databasePath;
    readonly ILogger<SQLiteOrchestrationService> _logger;
    readonly SemaphoreSlim _persistGate = new(1, 1);
    bool _hydrated;

    public SQLiteOrchestrationService(string databasePath, ILoggerFactory? loggerFactory = null)
        : base(loggerFactory)
    {
        _databasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<SQLiteOrchestrationService>();
    }

    public string DatabasePath => _databasePath;

    public new Task StartAsync()
    {
        EnsureSchemaSync();
        HydrateSync();
        _hydrated = true;
        return base.StartAsync();
    }

    public new async Task StopAsync(bool isForced)
    {
        // Best-effort final snapshot for graceful shutdowns.
        try
        {
            await PersistAllAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Final persistence on StopAsync failed");
        }

        await base.StopAsync(isForced).ConfigureAwait(false);
    }

    public new Task CreateAsync(bool recreateInstanceStore)
    {
        var t = base.CreateAsync(recreateInstanceStore);
        if (recreateInstanceStore)
        {
            ClearDatabaseSync();
        }
        return t;
    }

    public new Task DeleteAsync(bool deleteInstanceStore)
    {
        var t = base.DeleteAsync(deleteInstanceStore);
        if (deleteInstanceStore)
        {
            ClearDatabaseSync();
        }
        return t;
    }

    public new Task CreateTaskOrchestrationAsync(TaskMessage creationMessage) =>
        SnapshotAfter(() => base.CreateTaskOrchestrationAsync(creationMessage));

    public new Task CreateTaskOrchestrationAsync(TaskMessage creationMessage, OrchestrationStatus[]? dedupeStatuses) =>
        SnapshotAfter(() => base.CreateTaskOrchestrationAsync(creationMessage, dedupeStatuses));

    public new Task SendTaskOrchestrationMessageAsync(TaskMessage message) =>
        SnapshotAfter(() => base.SendTaskOrchestrationMessageAsync(message));

    public new Task SendTaskOrchestrationMessageBatchAsync(params TaskMessage[] messages) =>
        SnapshotAfter(() => base.SendTaskOrchestrationMessageBatchAsync(messages));

    public new Task CompleteTaskActivityWorkItemAsync(TaskActivityWorkItem workItem, TaskMessage responseMessage) =>
        SnapshotAfter(() => base.CompleteTaskActivityWorkItemAsync(workItem, responseMessage));

    public new Task CompleteTaskOrchestrationWorkItemAsync(
        TaskOrchestrationWorkItem workItem,
        OrchestrationRuntimeState newOrchestrationRuntimeState,
        IList<TaskMessage> outboundMessages,
        IList<TaskMessage> orchestratorMessages,
        IList<TaskMessage> timerMessages,
        TaskMessage continuedAsNewMessage,
        OrchestrationState orchestrationState) =>
        SnapshotAfter(() => base.CompleteTaskOrchestrationWorkItemAsync(
            workItem,
            newOrchestrationRuntimeState,
            outboundMessages,
            orchestratorMessages,
            timerMessages,
            continuedAsNewMessage,
            orchestrationState));

    public new Task ForceTerminateTaskOrchestrationAsync(string instanceId, string reason) =>
        SnapshotAfter(() => base.ForceTerminateTaskOrchestrationAsync(instanceId, reason));

    async Task SnapshotAfter(Func<Task> op)
    {
        await op().ConfigureAwait(false);
        if (_hydrated)
        {
            try
            {
                await PersistAllAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Snapshot persist failed");
            }
        }
    }

    void EnsureSchemaSync()
    {
        var dir = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var conn = new SqliteConnection($"Data Source={_databasePath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS orchestration_state (
                instance_id     TEXT PRIMARY KEY,
                execution_id    TEXT,
                is_completed    INTEGER NOT NULL,
                status_json     TEXT,
                history_json    TEXT NOT NULL,
                messages_json   TEXT NOT NULL,
                updated_at      TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    void ClearDatabaseSync()
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_databasePath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM orchestration_state;";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ClearDatabase failed");
        }
    }

    async Task PersistAllAsync(CancellationToken ct)
    {
        await _persistGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var instanceStore = s_instanceStoreField.GetValue(this)
                ?? throw new InvalidOperationException("instanceStore is null");
            var store = (ConcurrentDictionary<string, object>)s_innerStoreField.GetValue(instanceStore)!;

            await using var conn = new SqliteConnection($"Data Source={_databasePath}");
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

            foreach (var kvp in store)
            {
                ct.ThrowIfCancellationRequested();
                await UpsertAsync(conn, tx, kvp.Key, kvp.Value, ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);

            // WAL checkpoint to bound journal growth
            await using var walCmd = conn.CreateCommand();
            walCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await walCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _persistGate.Release();
        }
    }

    static async Task UpsertAsync(SqliteConnection conn, SqliteTransaction tx, string instanceId, object state, CancellationToken ct)
    {
        var status = (JsonNode?)s_statusRecordField.GetValue(state);
        var history = (JsonNode?)s_historyField.GetValue(state);
        var messages = (JsonNode?)s_messagesField.GetValue(state);
        var executionId = (string?)s_executionIdField.GetValue(state);
        var isCompleted = (bool)s_isCompletedField.GetValue(state)!;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO orchestration_state (instance_id, execution_id, is_completed, status_json, history_json, messages_json, updated_at)
            VALUES ($id, $eid, $comp, $status, $history, $messages, $ts)
            ON CONFLICT(instance_id) DO UPDATE SET
                execution_id = excluded.execution_id,
                is_completed = excluded.is_completed,
                status_json = excluded.status_json,
                history_json = excluded.history_json,
                messages_json = excluded.messages_json,
                updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$id", instanceId);
        cmd.Parameters.AddWithValue("$eid", (object?)executionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$comp", isCompleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$status", (object?)status?.ToJsonString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$history", history?.ToJsonString() ?? "[]");
        cmd.Parameters.AddWithValue("$messages", messages?.ToJsonString() ?? "[]");
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    void HydrateSync()
    {
        var rows = ReadAllRows();
        if (rows.Count == 0)
        {
            return;
        }

        var instanceStore = s_instanceStoreField.GetValue(this)
            ?? throw new InvalidOperationException("instanceStore is null");
        var store = (ConcurrentDictionary<string, object>)s_innerStoreField.GetValue(instanceStore)!;
        var readyToRunQueue = s_readyToRunQueueField.GetValue(instanceStore)
            ?? throw new InvalidOperationException("readyToRunQueue is null");

        foreach (var row in rows)
        {
            var instanceId = row.InstanceId;
            var executionId = row.ExecutionId;
            var state = s_serializedInstanceStateCtor.Invoke(new object?[] { instanceId, executionId });

            if (!string.IsNullOrEmpty(row.StatusJson))
            {
                s_statusRecordField.SetValue(state, JsonNode.Parse(row.StatusJson));
            }

            var history = string.IsNullOrEmpty(row.HistoryJson)
                ? new JsonArray()
                : JsonNode.Parse(row.HistoryJson) as JsonArray ?? new JsonArray();
            s_historyField.SetValue(state, history);

            var messages = string.IsNullOrEmpty(row.MessagesJson)
                ? new JsonArray()
                : JsonNode.Parse(row.MessagesJson) as JsonArray ?? new JsonArray();
            s_messagesField.SetValue(state, messages);

            s_isCompletedField.SetValue(state, row.IsCompleted);

            store[instanceId] = state;

            if (!row.IsCompleted && messages.Count > 0)
            {
                s_scheduleMethod.Invoke(readyToRunQueue, new[] { state });
            }
        }

        _logger.LogInformation("Hydrated {Count} orchestration instance(s) from SQLite", rows.Count);
    }

    List<PersistedRow> ReadAllRows()
    {
        var rows = new List<PersistedRow>();
        using var conn = new SqliteConnection($"Data Source={_databasePath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT instance_id, execution_id, is_completed, status_json, history_json, messages_json
            FROM orchestration_state;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new PersistedRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt32(2) != 0,
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return rows;
    }

    sealed record PersistedRow(
        string InstanceId,
        string? ExecutionId,
        bool IsCompleted,
        string? StatusJson,
        string? HistoryJson,
        string? MessagesJson);
}
