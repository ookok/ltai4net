using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Memory;

public sealed class RefsSearchIndex : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly ILogger<RefsSearchIndex> _logger;
    private readonly string _refsDir;

    public RefsSearchIndex(string dbPath, string refsDir, ILogger<RefsSearchIndex>? logger = null)
    {
        _refsDir = refsDir;
        _logger = logger ?? NullLogger<RefsSearchIndex>.Instance;

        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString());
        _conn.Open();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE VIRTUAL TABLE IF NOT EXISTS refs_fts USING fts5(
                filename, content, tool_name, trace_id,
                tokenize='unicode61'
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task IndexFileAsync(string filePath)
    {
        try
        {
            var content = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
            var name = Path.GetFileName(filePath);
            var toolName = ExtractToolName(content);
            var traceId = ExtractTraceId(content);

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO refs_fts (filename, content, tool_name, trace_id) VALUES ($fn, $ct, $tn, $ti)";
            cmd.Parameters.AddWithValue("$fn", name);
            cmd.Parameters.AddWithValue("$ct", content);
            cmd.Parameters.AddWithValue("$tn", toolName ?? "");
            cmd.Parameters.AddWithValue("$ti", traceId ?? "");
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RefsSearchIndex: failed to index {File}", filePath);
        }
    }

    public async Task IndexDirectoryAsync()
    {
        if (!Directory.Exists(_refsDir)) return;
        var files = Directory.GetFiles(_refsDir, "*.md");
        foreach (var f in files) await IndexFileAsync(f).ConfigureAwait(false);
        _logger.LogInformation("RefsSearchIndex: indexed {Count} files", files.Length);
    }

    public async Task<List<RefsSearchResult>> SearchAsync(string query, int topK = 10)
    {
        var results = new List<RefsSearchResult>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT filename, content, tool_name, rank FROM refs_fts WHERE refs_fts MATCH $q ORDER BY rank LIMIT $lim";
        cmd.Parameters.AddWithValue("$q", query);
        cmd.Parameters.AddWithValue("$lim", topK);
        using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (rdr.Read())
        {
            results.Add(new RefsSearchResult(
                Filename: rdr.GetString(0),
                ContentSnippet: Truncate(rdr.GetString(1), 300),
                ToolName: rdr.IsDBNull(2) ? "" : rdr.GetString(2),
                Rank: rdr.GetDouble(3)));
        }
        return results;
    }

    public void Dispose() => _conn.Dispose();

    private static string? ExtractToolName(string content)
    {
        foreach (var line in content.Split('\n'))
            if (line.StartsWith("# "))
                return line[2..].Trim();
        return null;
    }

    private static string? ExtractTraceId(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("- **TraceId**: `"))
            {
                var start = trimmed.IndexOf('`') + 1;
                var end = trimmed.LastIndexOf('`');
                return start > 0 && end > start ? trimmed[start..end] : null;
            }
        }
        return null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}

public sealed record RefsSearchResult(string Filename, string ContentSnippet, string ToolName, double Rank);
