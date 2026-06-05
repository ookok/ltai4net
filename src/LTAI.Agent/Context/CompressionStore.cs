using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace LTAI.Agent.Context;

public sealed record CompressedEntry(
    string Id,
    string Original,
    string Summary,
    int OriginalTokens,
    string ContentType);

public sealed class CompressionStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private long _totalEntries;

    public CompressionStore(string? dbPath = null)
    {
        dbPath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".livingtree", "compression.db");

        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS compression_store (
                id             TEXT PRIMARY KEY,
                content_hash   TEXT NOT NULL,
                original       TEXT NOT NULL,
                summary        TEXT NOT NULL DEFAULT '',
                original_tokens INTEGER NOT NULL DEFAULT 0,
                content_type   TEXT NOT NULL DEFAULT 'text',
                compressed_at  TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_compression_hash ON compression_store(content_hash);
            """;
        cmd.ExecuteNonQuery();

        using var countCmd = _conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM compression_store";
        _totalEntries = (long)(countCmd.ExecuteScalar() ?? 0);
    }

    public string Store(string content, string summary, ContentCompressor.ContentType type)
    {
        var hash = ComputeHash(content);
        var id = hash[..12];

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO compression_store
                (id, content_hash, original, summary, original_tokens, content_type, compressed_at)
            VALUES ($id, $hash, $original, $summary, $tokens, $type, $at)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$original", content);
        cmd.Parameters.AddWithValue("$summary", summary);
        cmd.Parameters.AddWithValue("$tokens", EstimateTokens(content));
        cmd.Parameters.AddWithValue("$type", type.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));

        if (cmd.ExecuteNonQuery() > 0)
            Interlocked.Increment(ref _totalEntries);

        return id;
    }

    public string? Retrieve(string id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT original FROM compression_store WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() as string;
    }

    public CompressedEntry? GetEntry(string id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, original, summary, original_tokens, content_type
            FROM compression_store WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new CompressedEntry(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetString(4));
    }

    public int Cleanup(TimeSpan maxAge)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM compression_store WHERE compressed_at < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", DateTime.UtcNow.Subtract(maxAge).ToString("O"));
        var deleted = cmd.ExecuteNonQuery();
        Interlocked.Add(ref _totalEntries, -deleted);
        return deleted;
    }

    public long TotalEntries => _totalEntries;

    public void Dispose() => _conn.Dispose();

    private static string ComputeHash(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    internal static int EstimateTokens(string text) => Math.Max(1, text.Length / 4);
}
