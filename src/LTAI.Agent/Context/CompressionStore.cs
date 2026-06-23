// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  CompressionStore — reversible compression store (in kg.db)
//  OPTIMIZED: shares kg.db via CreateShared().
// ═══════════════════════════════════════════════════════════════

using System.Data;
using System.Text;
using LTAI.Core.Configuration;
using Microsoft.Data.Sqlite;

namespace LTAI.Agent.Context;

public sealed record CompressedEntry(string Id, string Original, string Summary, int OriginalTokens, string ContentType);

public sealed class CompressionStore : IDisposable
{
    private readonly string _connectionString;
    private SqliteConnection? _sharedConn;
    private bool _schemaReady;
    private readonly object _gate = new();
    private long _totalEntries;
    private long _storeCount;
    private static readonly TimeSpan MaxEntryAge = TimeSpan.FromDays(EnvironmentConfig.CompressionMaxAgeDays);

    public CompressionStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared,
        }.ToString();
        EnsureSchema();
    }

    public static CompressionStore CreateShared(string kgDbPath) => new(kgDbPath);

    private SqliteConnection GetConnection()
    {
        if (_sharedConn != null && _sharedConn.State == ConnectionState.Open)
            return _sharedConn;
        lock (_gate)
        {
            if (_sharedConn != null && _sharedConn.State == ConnectionState.Open)
                return _sharedConn;
            _sharedConn = new SqliteConnection(_connectionString);
            _sharedConn.Open();
            using var pragma = _sharedConn.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
            return _sharedConn;
        }
    }

    private void EnsureSchema()
    {
        if (_schemaReady) return;
        lock (_gate)
        {
            if (_schemaReady) return;
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS compression_store (
                    id TEXT PRIMARY KEY, content_hash TEXT NOT NULL, original TEXT NOT NULL,
                    summary TEXT NOT NULL DEFAULT '', original_tokens INTEGER NOT NULL DEFAULT 0,
                    content_type TEXT NOT NULL DEFAULT 'text', compressed_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_compression_hash ON compression_store(content_hash);
                """;
            cmd.ExecuteNonQuery();
            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM compression_store";
            _totalEntries = (long)(countCmd.ExecuteScalar() ?? 0);
            _schemaReady = true;
        }
    }

    public string Store(string content, string summary, ContentCompressor.ContentType type)
    {
        EnsureSchema();
        var hash = ComputeHash(content);
        var id = hash[..12];
        var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO compression_store (id,content_hash,original,summary,original_tokens,content_type,compressed_at) VALUES ($id,$hash,$original,$summary,$tokens,$type,$at)";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$original", content); cmd.Parameters.AddWithValue("$summary", summary);
        cmd.Parameters.AddWithValue("$tokens", EstimateTokens(content));
        cmd.Parameters.AddWithValue("$type", type.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
        if (cmd.ExecuteNonQuery() > 0)
        {
            Interlocked.Increment(ref _totalEntries);
            if (Interlocked.Increment(ref _storeCount) % 100 == 0) Cleanup(MaxEntryAge);
        }
        return id;
    }

    public string? Retrieve(string id)
    {
        EnsureSchema();
        var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT original FROM compression_store WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() as string;
    }

    public CompressedEntry? GetEntry(string id)
    {
        EnsureSchema();
        var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id,original,summary,original_tokens,content_type FROM compression_store WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new CompressedEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4));
    }

    public int Cleanup(TimeSpan maxAge)
    {
        EnsureSchema();
        var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM compression_store WHERE compressed_at < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", DateTime.UtcNow.Subtract(maxAge).ToString("O"));
        var deleted = cmd.ExecuteNonQuery();
        Interlocked.Add(ref _totalEntries, -deleted);
        return deleted;
    }

    public long TotalEntries => Interlocked.Read(ref _totalEntries);
    public void Dispose()
    {
        _sharedConn?.Close();
        _sharedConn?.Dispose();
        _sharedConn = null;
    }

    private static string ComputeHash(string content) => FastHash.ComputeHex(content);
    internal static int EstimateTokens(string text) => TokenEstimator.Estimate(text);
}
