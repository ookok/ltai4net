using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LTAI.Core.Storage;

namespace LTAI.Agent.Vector;

public sealed partial class KgStore
{
    // ═══════════════════════════════════════════
    //  Maintenance
    // ═══════════════════════════════════════════

    public async Task<(int pruned, long beforeBytes, long afterBytes)> RunMaintenanceAsync(
        string rootDir, TimeSpan? timeToLive = null)
    {
        var before = new FileInfo(_dbPath).Length;
        var pruned = 0;

        if (timeToLive.HasValue)
        {
            var cutoff = DateTime.UtcNow - timeToLive.Value;
            pruned += await PruneBefore(cutoff).ConfigureAwait(false);
        }

        if (pruned > 0)
            await CompactAsync().ConfigureAwait(false);

        _resultCache.Compact(1.0);

        var after = new FileInfo(_dbPath).Length;
        return (pruned, before, after);
    }

    public async Task<int> PruneBefore(DateTime cutoff)
    {
        var cutoffStr = cutoff.ToString("O");
        var count = await WriteLockAsync(async cmd => (int)(long)(await cmd.ExecuteScalarAsync().ConfigureAwait(false))!,
            "SELECT COUNT(*) FROM Nodes WHERE updated_at < @cutoff;",
            cmd => cmd.Parameters.AddWithValue("@cutoff", cutoffStr)).ConfigureAwait(false);

        if (count > 0)
        {
            await WriteLockVoidAsync("DELETE FROM Nodes WHERE updated_at < @cutoff;",
                cmd => cmd.Parameters.AddWithValue("@cutoff", cutoffStr)).ConfigureAwait(false);
            await RebuildCentroidsAsync().ConfigureAwait(false);
        }
        return count;
    }

    public async Task OptimizeFtsAsync()
    {
        await WriteLockVoidAsync("INSERT INTO FTS_Index(FTS_Index) VALUES('optimize');").ConfigureAwait(false);
    }

    public async Task<int> RebuildFtsAsync()
    {
        return await WriteLockAsync(async cmd =>
        {
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            cmd.CommandText = "SELECT COUNT(*) FROM FTS_Index;";
            return (int)(long)(await cmd.ExecuteScalarAsync().ConfigureAwait(false))!;
        }, "DELETE FROM FTS_Index; INSERT INTO FTS_Index SELECT text, node_id, kind FROM Docs JOIN Nodes ON Nodes.id = Docs.node_id;").ConfigureAwait(false);
    }

    public async Task CompactAsync()
    {
        ThrowIfDisposed();
        bool acquired = false;
        if (!_ownsWriteLock.Value)
        {
            await _writeLock.WaitAsync().ConfigureAwait(false);
            _ownsWriteLock.Value = true;
            acquired = true;
        }
        try
        {
            using var vacCmd = _writer.CreateCommand();
            vacCmd.CommandText = "VACUUM;";
            await vacCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            using var optCmd = _writer.CreateCommand();
            optCmd.CommandText = "PRAGMA optimize;";
            await optCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            await OptimizeFtsAsync().ConfigureAwait(false);
            _resultCache.Compact(0.5);
        }
        finally { if (acquired) { _ownsWriteLock.Value = false; _writeLock.Release(); } }
    }

    // ═══════════════════════════════════════════
    //  Stats
    // ═══════════════════════════════════════════

    public async Task<string> Stats()
    {
        var n = await NodeCount().ConfigureAwait(false);
        var e = await CountEdges().ConfigureAwait(false);
        var d = await CountDocs().ConfigureAwait(false);
        var kinds = await GetKinds().ConfigureAwait(false);
        var info = new FileInfo(_dbPath);

        return $"Nodes: {n}, Edges: {e}, Docs: {d}, Size: {FormatBytes(info.Length)}\n"
             + $"Kinds: {string.Join(", ", kinds)}";
    }

    private async Task<long> CountEdges()
    {
        const string sql = "SELECT COUNT(*) FROM Edges;";
        using var cmd = CreateReadCommand(sql);
        return (long)(await cmd.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private async Task<long> CountDocs()
    {
        const string sql = "SELECT COUNT(*) FROM Docs;";
        using var cmd = CreateReadCommand(sql);
        return (long)(await cmd.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private async Task<List<string>> GetKinds()
    {
        using var cmd = _reader.CreateCommand();
        cmd.CommandText = "SELECT kind, COUNT(*) FROM Nodes GROUP BY kind ORDER BY COUNT(*) DESC;";
        var kinds = new List<string>();
        using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (rdr.Read())
            kinds.Add($"{rdr.GetString(0)}: {rdr.GetInt32(1)}");
        return kinds;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024}KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1}MB"
    };

    // ═══════════════════════════════════════════
    //  Controlled vocabulary + version history
    // ═══════════════════════════════════════════

    private const string SQL_SAVE_VERSION = """
        INSERT INTO Versions(node_id, kind, name, snapshot, reason)
        VALUES (@nid, @kind, @name, @snap, @reason);
        """;

    public async Task SaveVersionAsync(long nodeId, string kind, string name,
        Dictionary<string, object?> snapshot, string? reason = null)
    {
        var json = JsonSerializer.Serialize(snapshot, KgStoreInternals.JsonOpts);
        await WriteLockVoidAsync(SQL_SAVE_VERSION, cmd =>
        {
            cmd.Parameters.AddWithValue("@nid", nodeId);
            cmd.Parameters.AddWithValue("@kind", kind);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@snap", json);
            cmd.Parameters.AddWithValue("@reason", (object?)reason ?? DBNull.Value);
        }).ConfigureAwait(false);
    }

    private const string SQL_GET_VERSIONS =
        "SELECT * FROM Versions WHERE node_id = @nid ORDER BY id DESC;";

    public async Task<List<VersionRow>> GetVersionsAsync(long nodeId)
    {
        using var cmd = CreateReadCommand(SQL_GET_VERSIONS);
        cmd.Parameters.AddWithValue("@nid", nodeId);
        return ReadVersionRows(await cmd.ExecuteReaderAsync().ConfigureAwait(false));
    }

    public async Task EditNodeAsync(long nodeId, string? name = null,
        string? kind = null, string? signature = null,
        string? source = null, string? reason = null)
    {
        var existing = await GetNode(nodeId).ConfigureAwait(false);
        if (existing == null) return;

        if (reason != null)
        {
            var snap = new Dictionary<string, object?>
            {
                ["name"] = existing.Name,
                ["kind"] = existing.Kind,
                ["signature"] = existing.Signature,
                ["source"] = existing.Source,
                ["props"] = existing.Props,
            };
            await SaveVersionAsync(nodeId, existing.Kind, existing.Name, snap, reason)
                .ConfigureAwait(false);
        }

        var parts = new List<string> { "updated_at = strftime('%Y-%m-%dT%H:%M:%SZ','now')" };
        if (name != null) parts.Add("name = @name");
        if (kind != null) parts.Add("kind = @kind");
        if (signature != null) parts.Add("signature = @sig");
        if (source != null) parts.Add("source = @src");
        if (parts.Count == 1) return;

        var sql = $"UPDATE Nodes SET {string.Join(", ", parts)} WHERE id = @nid;";
        await WriteLockVoidAsync(sql, cmd =>
        {
            if (name != null) cmd.Parameters.AddWithValue("@name", name);
            if (kind != null) cmd.Parameters.AddWithValue("@kind", kind);
            if (signature != null) cmd.Parameters.AddWithValue("@sig", signature);
            if (source != null) cmd.Parameters.AddWithValue("@src", source);
            cmd.Parameters.AddWithValue("@nid", nodeId);
        }).ConfigureAwait(false);
    }

    // ═══════════════════════════════════════════
    //  LocalVersionRepo — export / import
    // ═══════════════════════════════════════════

    private const string SQL_GET_ALL_NODES = "SELECT * FROM Nodes ORDER BY id;";

    public async Task<List<NodeRow>> GetAllNodes()
    {
        using var cmd = _reader.CreateCommand();
        cmd.CommandText = SQL_GET_ALL_NODES;
        return ReadNodeRows(await cmd.ExecuteReaderAsync().ConfigureAwait(false));
    }

    private const string SQL_GET_ALL_DOCS = "SELECT * FROM Docs ORDER BY id;";

    public async Task<List<DocRow>> GetAllDocs()
    {
        using var cmd = _reader.CreateCommand();
        cmd.CommandText = SQL_GET_ALL_DOCS;
        return ReadDocRows(await cmd.ExecuteReaderAsync().ConfigureAwait(false));
    }

    public async Task<string> ExportAllToRepoAsync(string label)
    {
        var prefix = Path.Combine("kg", label);

        var nodes = await GetAllNodes().ConfigureAwait(false);
        var edges = await GetEdges(null).ConfigureAwait(false);
        var docs = await GetAllDocs().ConfigureAwait(false);

        var nodesJson = JsonSerializer.Serialize(
            nodes.Select(n => new
            {
                n.Id, n.ExtId, n.Kind, n.Name, n.Namespace,
                n.Signature, n.Source, n.Props, n.CreatedAt, n.UpdatedAt
            }), KgStoreInternals.JsonOpts);

        var edgesJson = JsonSerializer.Serialize(
            edges.Select(e => new
            {
                e.Id, e.Src, e.Dst, e.Relation, e.Weight, e.Props
            }), KgStoreInternals.JsonOpts);

        var docsJson = JsonSerializer.Serialize(
            docs.Select(d => new
            {
                d.Id, d.NodeId, d.Text, d.Lang, d.Source
            }), KgStoreInternals.JsonOpts);

        LocalVersionRepo.Default.AtomicCommit(
            $"{prefix}/nodes.json", nodesJson, $"KG export: {label} — {nodes.Count} nodes");
        LocalVersionRepo.Default.AtomicCommit(
            $"{prefix}/edges.json", edgesJson, $"KG export: {label} — {edges.Count} edges");
        return LocalVersionRepo.Default.AtomicCommit(
            $"{prefix}/docs.json", docsJson, $"KG export: {label} — {docs.Count} docs");
    }

    // ═══════════════════════════════════════════
    //  IDisposable
    // ═══════════════════════════════════════════

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var lazy in _writeCmdCache.Values) { if (lazy.IsValueCreated) lazy.Value.Dispose(); }
        _writeCmdCache.Clear();
        foreach (var cmd in _readCmdCache.Values) cmd.Dispose();
        _readCmdCache.Clear();
        _resultCache.Dispose();
        _hnsw.Dispose();
        _hnswLock.Dispose();

        _writer.Dispose();
        _reader.Dispose();
        _writeLock.Dispose();
    }
}
