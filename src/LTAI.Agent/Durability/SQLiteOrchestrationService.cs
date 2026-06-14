// Copyright (c) LTAI. All rights reserved.
#pragma warning disable IL2075, IL2080 // NativeAOT IL — DTFx reflection-based serialization

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
    static readonly FieldInfo? s_instanceStoreField =
        typeof(InMemoryOrchestrationService).GetField("instanceStore", BindingFlags.NonPublic | BindingFlags.Instance);

    static readonly FieldInfo? s_innerStoreField = s_instanceStoreField?.FieldType
        .GetField("store", BindingFlags.NonPublic | BindingFlags.Instance);

    static readonly Type? s_serializedInstanceStateType = s_innerStoreField?.FieldType.GetGenericArguments()?.ElementAtOrDefault(1);

    static readonly FieldInfo? s_statusRecordField =
        s_serializedInstanceStateType?.GetField("StatusRecordJson", BindingFlags.Public | BindingFlags.Instance);

    static readonly FieldInfo? s_historyField =
        s_serializedInstanceStateType?.GetField("HistoryEventsJson", BindingFlags.Public | BindingFlags.Instance);

    static readonly FieldInfo? s_messagesField =
        s_serializedInstanceStateType?.GetField("MessagesJson", BindingFlags.Public | BindingFlags.Instance);

    static readonly FieldInfo? s_executionIdField =
        s_serializedInstanceStateType?.GetField("ExecutionId", BindingFlags.Public | BindingFlags.Instance);

    static readonly FieldInfo? s_isCompletedField =
        s_serializedInstanceStateType?.GetField("IsCompleted", BindingFlags.NonPublic | BindingFlags.Instance);

    static readonly FieldInfo? s_readyToRunQueueField = s_instanceStoreField?.FieldType
        .GetField("readyToRunQueue", BindingFlags.NonPublic | BindingFlags.Instance);

    static readonly MethodInfo? s_scheduleMethod = s_readyToRunQueueField?.FieldType
        .GetMethod("Schedule", BindingFlags.Public | BindingFlags.Instance);

    static readonly ConstructorInfo? s_serializedInstanceStateCtor = s_serializedInstanceStateType?
        .GetConstructor(new[] { typeof(string), typeof(string) });

    bool _reflectionOk;

    static SQLiteOrchestrationService()
    {
        if (s_instanceStoreField == null || s_innerStoreField == null || s_serializedInstanceStateType == null ||
            s_statusRecordField == null || s_historyField == null || s_messagesField == null ||
            s_executionIdField == null || s_isCompletedField == null || s_readyToRunQueueField == null ||
            s_scheduleMethod == null || s_serializedInstanceStateCtor == null)
        {
            // MAF SDK update may have changed internal fields — persistence degrades gracefully
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SQLiteOrchestrationService>.Instance
                .LogWarning("DTFx reflection bindings incomplete — orchestration persistence disabled");
        }
    }

    readonly string _databasePath;
    readonly ILogger<SQLiteOrchestrationService> _logger;
    readonly SemaphoreSlim _persistGate = new(1, 1);
    bool _hydrated;

    // Batch persistence: debounce writes within a 500ms window
    private CancellationTokenSource _batchCts = new();
    private int _pendingPersistCount;
    private bool _persistScheduled;
    private static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(500);

    public SQLiteOrchestrationService(string databasePath, ILoggerFactory? loggerFactory = null)
        : base(loggerFactory)
    {
        _databasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<SQLiteOrchestrationService>();
        _reflectionOk = s_instanceStoreField != null && s_innerStoreField != null
            && s_serializedInstanceStateType != null && s_serializedInstanceStateCtor != null
            && s_statusRecordField != null && s_historyField != null && s_messagesField != null
            && s_executionIdField != null && s_isCompletedField != null;
        if (!_reflectionOk)
            _logger.LogWarning("DTFx reflection bindings incomplete — orchestration persistence disabled");
    }

    public string DatabasePath => _databasePath;

    public new Task StartAsync()
    {
        EnsureSchemaSync();
        HydrateSync();
        _hydrated = true;
        // Replace the batch CTS in case this instance was stopped and restarted
        var oldCts = Interlocked.Exchange(ref _batchCts, new CancellationTokenSource());
        try { oldCts.Dispose(); } catch { }
        return base.StartAsync();
    }

    public new async Task StopAsync(bool isForced)
    {
        // Cancel any pending batch timer and flush
        _batchCts.Cancel();

        var pending = Interlocked.Exchange(ref _pendingPersistCount, 0);
        if (pending > 0)
        {
            try
            {
                await PersistAllAsync(CancellationToken.None).ConfigureAwait(false);
                _logger.LogTrace("Final flush: {Count} pending writes persisted", pending);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Final flush on StopAsync failed");
            }
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

    public new async Task PurgeOrchestrationHistoryAsync(DateTime thresholdDateTimeUtc,
        OrchestrationStateTimeRangeFilterType timeRangeFilterType)
    {
        if (!_hydrated || !_reflectionOk) return;
        if (!File.Exists(_databasePath)) return;

        try
        {
            await using var conn = new SqliteConnection($"Data Source={_databasePath}");
            await conn.OpenAsync().ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            // Delete completed orchestration instances before the threshold.
            // Non-completed instances are preserved regardless of age.
            cmd.CommandText = """
                DELETE FROM orchestration_state
                WHERE is_completed = 1
                  AND updated_at < $threshold;
                """;
            cmd.Parameters.AddWithValue("$threshold", thresholdDateTimeUtc.ToString("O"));
            var deleted = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            if (deleted > 0)
                _logger.LogInformation("Purged {Count} completed orchestration(s) before {Threshold}", deleted, thresholdDateTimeUtc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to purge orchestration history");
        }
    }

    async Task SnapshotAfter(Func<Task> op)
    {
        await op().ConfigureAwait(false);
        if (_hydrated)
        {
            ScheduleBatchPersist();
        }
    }

    /// <summary>
    /// Schedule a debounced batch persist. Multiple writes within 500ms
    /// are coalesced into a single SQLite transaction.
    /// For critical operations, call <see cref="PersistImmediateAsync"/> instead.
    /// </summary>
    void ScheduleBatchPersist()
    {
        Interlocked.Increment(ref _pendingPersistCount);
        if (Interlocked.CompareExchange(ref _persistScheduled, true, false))
            return; // Already scheduled

        // Fire-and-forget: wait for batch interval, then persist once
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(BatchInterval, _batchCts.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref _persistScheduled, false);
                var count = Interlocked.Exchange(ref _pendingPersistCount, 0);
                try
                {
                    await PersistAllAsync(CancellationToken.None).ConfigureAwait(false);
                    if (count > 1)
                        _logger.LogTrace("Batch persist: {Count} writes coalesced", count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Batch persist failed ({Writes} queued)", count);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>
    /// Immediate persist: flush all pending writes synchronously.
    /// Use for critical operations that must survive a crash.
    /// </summary>
    async Task PersistImmediateAsync(CancellationToken ct = default)
    {
        // Clear any pending batch
        Interlocked.Exchange(ref _persistScheduled, false);
        var count = Interlocked.Exchange(ref _pendingPersistCount, 0);
        try
        {
            await PersistAllAsync(ct).ConfigureAwait(false);
            if (count > 0)
                _logger.LogDebug("Immediate persist: {Count} writes flushed", count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Immediate persist failed ({Writes} queued)", count);
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
        if (!_reflectionOk) return;
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
        if (!_reflectionOk) return;
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
