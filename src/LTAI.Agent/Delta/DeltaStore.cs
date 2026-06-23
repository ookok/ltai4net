// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  DeltaStore — content-addressable delta log (DeltaDB-inspired)
//
//  Records every agent file edit as a fine-grained, addressable delta.
//  Deltas form a per-file DAG via ParentId, enabling full replay.
//  SQLite-backed, lives alongside kg.db in the data directory.
//
//  Key design:
//    - Each delta is identified by SHA256(content + parent + metadata)
//    - ParentId forms the delta chain per file (like Git but per-op)
//    - CodeProvenance maps (file,line) → deltaId → conversation
// ═══════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Delta;

public sealed class DeltaStore : IDisposable
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS deltas (
            id TEXT PRIMARY KEY,
            parent_id TEXT,
            conversation_id TEXT NOT NULL,
            message_id TEXT NOT NULL,
            tool_name TEXT NOT NULL,
            file_path TEXT NOT NULL,
            start_line INTEGER NOT NULL DEFAULT 0,
            end_line INTEGER NOT NULL DEFAULT 0,
            timestamp INTEGER NOT NULL,
            agent_id TEXT,
            diff_content TEXT,
            is_new_file INTEGER NOT NULL DEFAULT 0,
            checksum TEXT,
            metadata TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_deltas_file ON deltas(file_path);
        CREATE INDEX IF NOT EXISTS idx_deltas_conversation ON deltas(conversation_id);
        CREATE INDEX IF NOT EXISTS idx_deltas_message ON deltas(message_id);
        CREATE INDEX IF NOT EXISTS idx_deltas_parent ON deltas(parent_id);
        CREATE INDEX IF NOT EXISTS idx_deltas_timestamp ON deltas(timestamp DESC);
        CREATE INDEX IF NOT EXISTS idx_deltas_agent ON deltas(agent_id);
        CREATE INDEX IF NOT EXISTS idx_deltas_file_time ON deltas(file_path, timestamp DESC);
        """;

    private const string ProvenanceSchema = """
        CREATE TABLE IF NOT EXISTS delta_provenance (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            delta_id TEXT NOT NULL REFERENCES deltas(id),
            file_path TEXT NOT NULL,
            line_number INTEGER NOT NULL,
            conversation_id TEXT NOT NULL,
            message_id TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_prov_file_line ON delta_provenance(file_path, line_number);
        CREATE INDEX IF NOT EXISTS idx_prov_delta ON delta_provenance(delta_id);
        CREATE INDEX IF NOT EXISTS idx_prov_conversation ON delta_provenance(conversation_id);
        """;

    private const string ConvCodeSchema = """
        CREATE TABLE IF NOT EXISTS conversation_code (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            conversation_id TEXT NOT NULL,
            message_id TEXT NOT NULL,
            file_path TEXT NOT NULL,
            start_line INTEGER NOT NULL,
            end_line INTEGER NOT NULL,
            delta_id TEXT NOT NULL REFERENCES deltas(id)
        );
        CREATE INDEX IF NOT EXISTS idx_convcode_conv ON conversation_code(conversation_id, message_id);
        CREATE INDEX IF NOT EXISTS idx_convcode_file ON conversation_code(file_path);
        """;

    private const string WorktreeSchema = """
        CREATE TABLE IF NOT EXISTS worktree_ops (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            file_path TEXT NOT NULL,
            site_id TEXT NOT NULL,
            op_clock INTEGER NOT NULL,
            op_type TEXT NOT NULL,
            position BLOB,
            content TEXT,
            delta_id TEXT REFERENCES deltas(id),
            applied_at INTEGER NOT NULL,
            UNIQUE(file_path, site_id, op_clock)
        );
        CREATE INDEX IF NOT EXISTS idx_wt_file ON worktree_ops(file_path);
        CREATE INDEX IF NOT EXISTS idx_wt_site ON worktree_ops(site_id, op_clock);
        """;

    private readonly string _dbPath;
    private readonly SqliteConnection _writer;
    private readonly SqliteConnection _reader;
    private readonly ILogger<DeltaStore> _logger;
    private bool _schemaReady;
    private readonly object _gate = new();

    // In-memory site ID for this process
    private static readonly string s_siteId = Environment.MachineName + "-" + Environment.ProcessId;

    public DeltaStore(string dbPath, ILogger<DeltaStore>? logger = null)
    {
        _dbPath = dbPath;
        _logger = logger ?? NullLogger<DeltaStore>.Instance;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _writer = CreateConnection();
        _reader = CreateConnection();
        EnsureSchema();
    }

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString());
        conn.Open();
        return conn;
    }

    private void EnsureSchema()
    {
        if (_schemaReady) return;
        lock (_gate)
        {
            if (_schemaReady) return;
            using var pragma = _writer.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000";
            pragma.ExecuteNonQuery();
            foreach (var s in new[] { Schema, ProvenanceSchema, ConvCodeSchema, WorktreeSchema })
            {
                using var cmd = _writer.CreateCommand();
                cmd.CommandText = s;
                cmd.ExecuteNonQuery();
            }
            _schemaReady = true;
        }
    }

    public async Task<string> RecordDeltaAsync(DeltaEntry entry)
    {
        EnsureSchema();
        var deltaId = ComputeDeltaId(entry);
        entry = entry with { Id = deltaId };

        using var cmd = _writer.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO deltas
                (id, parent_id, conversation_id, message_id, tool_name,
                 file_path, start_line, end_line, timestamp, agent_id,
                 diff_content, is_new_file, checksum, metadata)
            VALUES ($id,$parent,$conv,$msg,$tool,
                    $file,$sl,$el,$ts,$agent,
                    $diff,$new,$cs,$meta)
            """;
        cmd.Parameters.AddWithValue("$id", deltaId);
        cmd.Parameters.AddWithValue("$parent", (object?)entry.ParentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$conv", entry.ConversationId);
        cmd.Parameters.AddWithValue("$msg", entry.MessageId);
        cmd.Parameters.AddWithValue("$tool", entry.ToolName);
        cmd.Parameters.AddWithValue("$file", entry.FilePath);
        cmd.Parameters.AddWithValue("$sl", entry.StartLine);
        cmd.Parameters.AddWithValue("$el", entry.EndLine);
        cmd.Parameters.AddWithValue("$ts", entry.Timestamp);
        cmd.Parameters.AddWithValue("$agent", (object?)entry.AgentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$diff", (object?)entry.DiffContent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$new", entry.IsNewFile ? 1 : 0);
        cmd.Parameters.AddWithValue("$cs", (object?)entry.Checksum ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$meta", entry.Metadata != null ? JsonSerializer.Serialize(entry.Metadata) : DBNull.Value);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

        // Record provenance for each affected line
        await RecordProvenanceAsync(deltaId, entry).ConfigureAwait(false);

        // Record conversation→code link
        await RecordConversationCodeLinkAsync(entry).ConfigureAwait(false);

        _logger.LogDebug("DeltaStore: recorded delta {Id} for {File} L{Sl}-L{El}",
            deltaId[..12], entry.FilePath, entry.StartLine, entry.EndLine);

        return deltaId;
    }

    private async Task RecordProvenanceAsync(string deltaId, DeltaEntry entry)
    {
        var lines = new HashSet<int>();
        for (int l = entry.StartLine; l <= entry.EndLine; l++)
            lines.Add(l);

        using var cmd = _writer.CreateCommand();
        cmd.CommandText = "INSERT INTO delta_provenance (delta_id, file_path, line_number, conversation_id, message_id) VALUES ($did,$fp,$ln,$cid,$mid)";
        cmd.Parameters.AddWithValue("$did", deltaId);
        cmd.Parameters.AddWithValue("$fp", entry.FilePath);
        cmd.Parameters.AddWithValue("$cid", entry.ConversationId);
        cmd.Parameters.AddWithValue("$mid", entry.MessageId);
        var lnParam = cmd.Parameters.Add("$ln", SqliteType.Integer);

        foreach (var line in lines)
        {
            lnParam.Value = line;
            try { await cmd.ExecuteNonQueryAsync().ConfigureAwait(false); }
            catch { /* ignore dupes */ }
        }
    }

    private async Task RecordConversationCodeLinkAsync(DeltaEntry entry)
    {
        using var cmd = _writer.CreateCommand();
        cmd.CommandText = "INSERT INTO conversation_code (conversation_id, message_id, file_path, start_line, end_line, delta_id) VALUES ($cid,$mid,$fp,$sl,$el,$did)";
        cmd.Parameters.AddWithValue("$cid", entry.ConversationId);
        cmd.Parameters.AddWithValue("$mid", entry.MessageId);
        cmd.Parameters.AddWithValue("$fp", entry.FilePath);
        cmd.Parameters.AddWithValue("$sl", entry.StartLine);
        cmd.Parameters.AddWithValue("$el", entry.EndLine);
        cmd.Parameters.AddWithValue("$did", entry.Id);
        try { await cmd.ExecuteNonQueryAsync().ConfigureAwait(false); }
        catch { /* ignore dupes */ }
    }

    public DeltaEntry? GetDelta(string deltaId)
    {
        EnsureSchema();
        using var cmd = _reader.CreateCommand();
        cmd.CommandText = "SELECT * FROM deltas WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", deltaId);
        using var rdr = cmd.ExecuteReader();
        return rdr.Read() ? ReadDelta(rdr) : null;
    }

    public List<DeltaEntry> GetFileDeltas(string filePath, int limit = 100)
    {
        EnsureSchema();
        using var cmd = _reader.CreateCommand();
        cmd.CommandText = "SELECT * FROM deltas WHERE file_path=$fp ORDER BY timestamp DESC LIMIT $lim";
        cmd.Parameters.AddWithValue("$fp", filePath);
        cmd.Parameters.AddWithValue("$lim", limit);
        using var rdr = cmd.ExecuteReader();
        var results = new List<DeltaEntry>();
        while (rdr.Read()) results.Add(ReadDelta(rdr));
        return results;
    }

    public List<DeltaEntry> GetConversationDeltas(string conversationId)
    {
        EnsureSchema();
        using var cmd = _reader.CreateCommand();
        cmd.CommandText = "SELECT * FROM deltas WHERE conversation_id=$cid ORDER BY timestamp ASC";
        cmd.Parameters.AddWithValue("$cid", conversationId);
        using var rdr = cmd.ExecuteReader();
        var results = new List<DeltaEntry>();
        while (rdr.Read()) results.Add(ReadDelta(rdr));
        return results;
    }

    public List<CodeProvenanceResult> GetProvenanceForLines(string filePath, int startLine, int endLine)
    {
        EnsureSchema();
        using var cmd = _reader.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT p.delta_id, p.conversation_id, p.message_id,
                   p.file_path, p.line_number, d.tool_name, d.timestamp
            FROM delta_provenance p
            JOIN deltas d ON d.id = p.delta_id
            WHERE p.file_path=$fp AND p.line_number BETWEEN $sl AND $el
            ORDER BY d.timestamp DESC
            LIMIT 50
            """;
        cmd.Parameters.AddWithValue("$fp", filePath);
        cmd.Parameters.AddWithValue("$sl", startLine);
        cmd.Parameters.AddWithValue("$el", endLine);
        using var rdr = cmd.ExecuteReader();
        var results = new List<CodeProvenanceResult>();
        while (rdr.Read())
        {
            results.Add(new CodeProvenanceResult(
                DeltaId: rdr.GetString(0),
                ConversationId: rdr.GetString(1),
                MessageId: rdr.GetString(2),
                FilePath: rdr.GetString(3),
                StartLine: rdr.GetInt32(4),
                EndLine: rdr.GetInt32(4),
                ToolName: rdr.GetString(5),
                Timestamp: rdr.GetInt64(6)));
        }
        return results;
    }

    public List<ConversationCodeLink> GetConversationCodeLinks(string conversationId, string? messageId = null)
    {
        EnsureSchema();
        using var cmd = _reader.CreateCommand();
        var sql = "SELECT file_path, start_line, end_line, delta_id FROM conversation_code WHERE conversation_id=$cid";
        if (messageId != null) sql += " AND message_id=$mid";
        sql += " ORDER BY start_line ASC";
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$cid", conversationId);
        if (messageId != null) cmd.Parameters.AddWithValue("$mid", messageId);
        using var rdr = cmd.ExecuteReader();
        var results = new List<ConversationCodeLink>();
        while (rdr.Read())
        {
            results.Add(new ConversationCodeLink(
                FilePath: rdr.GetString(0),
                StartLine: rdr.GetInt32(1),
                EndLine: rdr.GetInt32(2),
                DeltaId: rdr.GetString(3)));
        }
        return results;
    }

    public List<DeltaEntry> GetAgentDeltas(string agentId, int limit = 50)
    {
        EnsureSchema();
        using var cmd = _reader.CreateCommand();
        cmd.CommandText = "SELECT * FROM deltas WHERE agent_id=$aid ORDER BY timestamp DESC LIMIT $lim";
        cmd.Parameters.AddWithValue("$aid", agentId);
        cmd.Parameters.AddWithValue("$lim", limit);
        using var rdr = cmd.ExecuteReader();
        var results = new List<DeltaEntry>();
        while (rdr.Read()) results.Add(ReadDelta(rdr));
        return results;
    }

    public DeltaChainInfo? GetChainInfo(string filePath)
    {
        EnsureSchema();
        using var cmd = _reader.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), MAX(timestamp), MIN(timestamp) FROM deltas WHERE file_path=$fp";
        cmd.Parameters.AddWithValue("$fp", filePath);
        using var rdr = cmd.ExecuteReader();
        if (!rdr.Read() || rdr.GetInt32(0) == 0) return null;
        var count = rdr.GetInt32(0);
        var last = rdr.GetInt64(1);
        var first = rdr.GetInt64(2);

        string? headId = null;
        using var headCmd = _reader.CreateCommand();
        headCmd.CommandText = "SELECT id FROM deltas WHERE file_path=$fp ORDER BY timestamp DESC LIMIT 1";
        headCmd.Parameters.AddWithValue("$fp", filePath);
        using var headRdr = headCmd.ExecuteReader();
        if (headRdr.Read()) headId = headRdr.GetString(0);

        string? earliestId = null;
        using var earliestCmd = _reader.CreateCommand();
        earliestCmd.CommandText = "SELECT id FROM deltas WHERE file_path=$fp ORDER BY timestamp ASC LIMIT 1";
        earliestCmd.Parameters.AddWithValue("$fp", filePath);
        using var earliestRdr = earliestCmd.ExecuteReader();
        if (earliestRdr.Read()) earliestId = earliestRdr.GetString(0);

        return new DeltaChainInfo(filePath, count, headId, earliestId, first, last);
    }

    public DeltaStats GetStats()
    {
        EnsureSchema();
        var stats = new DeltaStats();
        using var cmd = _reader.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM deltas";
        stats.TotalDeltas = Convert.ToInt32(cmd.ExecuteScalar());

        cmd.CommandText = "SELECT COUNT(DISTINCT file_path) FROM deltas";
        stats.TotalFiles = Convert.ToInt32(cmd.ExecuteScalar());

        cmd.CommandText = "SELECT COUNT(DISTINCT conversation_id) FROM deltas";
        stats.TotalConversations = Convert.ToInt32(cmd.ExecuteScalar());

        cmd.CommandText = "SELECT COUNT(DISTINCT agent_id) FROM deltas WHERE agent_id IS NOT NULL";
        stats.TotalAgents = Convert.ToInt32(cmd.ExecuteScalar());

        cmd.CommandText = "SELECT MIN(timestamp), MAX(timestamp) FROM deltas";
        using var rdr = cmd.ExecuteReader();
        if (rdr.Read())
        {
            stats.EarliestTimestamp = rdr.IsDBNull(0) ? 0 : rdr.GetInt64(0);
            stats.LatestTimestamp = rdr.IsDBNull(1) ? 0 : rdr.GetInt64(1);
        }

        cmd.CommandText = "SELECT file_path, COUNT(*) as cnt FROM deltas GROUP BY file_path ORDER BY cnt DESC LIMIT 20";
        using var frdr = cmd.ExecuteReader();
        while (frdr.Read()) stats.EditsPerFile[frdr.GetString(0)] = frdr.GetInt32(1);

        cmd.CommandText = "SELECT tool_name, COUNT(*) as cnt FROM deltas GROUP BY tool_name ORDER BY cnt DESC";
        using var trdr = cmd.ExecuteReader();
        while (trdr.Read()) stats.EditsPerTool[trdr.GetString(0)] = trdr.GetInt32(1);

        return stats;
    }

    public async Task<string?> FindConversationForCodeAsync(string filePath, int lineNumber)
    {
        EnsureSchema();
        using var cmd = _reader.CreateCommand();
        cmd.CommandText = "SELECT conversation_id FROM delta_provenance WHERE file_path=$fp AND line_number=$ln ORDER BY rowid DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$fp", filePath);
        cmd.Parameters.AddWithValue("$ln", lineNumber);
        var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return result as string;
    }

    public async Task<List<DeltaEntry>> FindDeltasForConversationMessageAsync(string conversationId, string messageId)
    {
        EnsureSchema();
        using var cmd = _reader.CreateCommand();
        cmd.CommandText = "SELECT * FROM deltas WHERE conversation_id=$cid AND message_id=$mid ORDER BY timestamp ASC";
        cmd.Parameters.AddWithValue("$cid", conversationId);
        cmd.Parameters.AddWithValue("$mid", messageId);
        using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        var results = new List<DeltaEntry>();
        while (rdr.Read()) results.Add(ReadDelta(rdr));
        return results;
    }

    public string? FindParentDeltaId(string filePath)
    {
        using var cmd = _reader.CreateCommand();
        cmd.CommandText = "SELECT id FROM deltas WHERE file_path=$fp ORDER BY timestamp DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$fp", filePath);
        return cmd.ExecuteScalar() as string;
    }

    public async Task<string> CreateDeltaForEditAsync(
        string filePath, int startLine, int endLine,
        string? diffContent, string toolName,
        string conversationId, string messageId,
        string? agentId = null, bool isNewFile = false)
    {
        var parentId = FindParentDeltaId(filePath);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        string? checksum = null;
        if (File.Exists(filePath))
        {
            var content = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
            checksum = HexHash(content, 12);
        }

        var entry = new DeltaEntry
        {
            ParentId = parentId,
            ConversationId = conversationId,
            MessageId = messageId,
            ToolName = toolName,
            FilePath = filePath,
            StartLine = startLine,
            EndLine = endLine,
            Timestamp = timestamp,
            AgentId = agentId,
            DiffContent = diffContent,
            IsNewFile = isNewFile,
            Checksum = checksum,
        };

        return await RecordDeltaAsync(entry).ConfigureAwait(false);
    }

    public string ComputeDeltaId(DeltaEntry entry)
    {
        var sb = new StringBuilder();
        sb.Append(entry.ParentId ?? "");
        sb.Append(entry.ConversationId);
        sb.Append(entry.MessageId);
        sb.Append(entry.ToolName);
        sb.Append(entry.FilePath);
        sb.Append(entry.StartLine);
        sb.Append(entry.EndLine);
        sb.Append(entry.Timestamp);
        sb.Append(entry.AgentId ?? "");
        sb.Append(entry.DiffContent ?? "");
        sb.Append(entry.IsNewFile);
        sb.Append(entry.Checksum ?? "");
        return HexHash(sb.ToString(), 32);
    }

    public string SiteId => s_siteId;

    public void Dispose()
    {
        _writer?.Dispose();
        _reader?.Dispose();
    }

    private static DeltaEntry ReadDelta(SqliteDataReader r)
    {
        return new DeltaEntry
        {
            Id = r.GetString(0),
            ParentId = r.IsDBNull(1) ? null : r.GetString(1),
            ConversationId = r.GetString(2),
            MessageId = r.GetString(3),
            ToolName = r.GetString(4),
            FilePath = r.GetString(5),
            StartLine = r.GetInt32(6),
            EndLine = r.GetInt32(7),
            Timestamp = r.GetInt64(8),
            AgentId = r.IsDBNull(9) ? null : r.GetString(9),
            DiffContent = r.IsDBNull(10) ? null : r.GetString(10),
            IsNewFile = r.GetInt32(11) == 1,
            Checksum = r.IsDBNull(12) ? null : r.GetString(12),
            Metadata = r.IsDBNull(13) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(13)),
        };
    }

    internal static string HexHash(string input, int length)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var hex = Convert.ToHexStringLower(hash);
        return hex[..Math.Min(length, hex.Length)];
    }
}
