using System.Text;
using System.Text.Json;
using LTAI.Core.System;
using Microsoft.Data.Sqlite;

namespace LTAI.Vector.Knowledge;

public sealed record CompiledTruthPage
{
    public string PageId { get; init; } = "";
    public string EntityType { get; init; } = "concept";
    public string EntityName { get; init; } = "";
    public string CompiledTruth { get; init; } = "";
    public double Confidence { get; init; } = 0.5;
    public int EvidenceCount { get; init; }
    public double CreatedAt { get; init; }
    public double UpdatedAt { get; init; }
    public int RevisionCount { get; init; }
    public string TruthHash { get; init; } = "";
    public List<string> Tags { get; init; } = new();
}

public sealed record TimelineEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string PageId { get; init; } = "";
    public string Content { get; init; } = "";
    public string Source { get; init; } = "unknown";
    public string Author { get; init; } = "system";
    public double Timestamp { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public double Confidence { get; init; } = 0.5;
    public string? PreviousTruthHash { get; init; }
    public string? NewTruthHash { get; init; }
}

public sealed record IngestResult
{
    public string PageId { get; init; } = "";
    public bool TruthChanged { get; init; }
    public double NewConfidence { get; init; }
    public int TimelineCount { get; init; }
    public string? DiffSummary { get; init; }
}

public sealed class CompiledTruthStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly string _dbPath;

    public CompiledTruthStore(DataPathResolver dataPath)
    {
        _dbPath = dataPath.GetPath("brain.db");
        var dir = global::System.IO.Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir) && !global::System.IO.Directory.Exists(dir))
            global::System.IO.Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={_dbPath}");
        _conn.Open();
        CreateTables();
    }

    private void CreateTables()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS compiled_pages (
                page_id TEXT NOT NULL UNIQUE,
                entity_type TEXT NOT NULL DEFAULT 'concept',
                entity_name TEXT NOT NULL,
                compiled_truth TEXT NOT NULL DEFAULT '',
                confidence REAL DEFAULT 0.5,
                evidence_count INTEGER DEFAULT 0,
                created_at REAL DEFAULT (unixepoch()),
                updated_at REAL DEFAULT (unixepoch()),
                revision_count INTEGER DEFAULT 1,
                tags TEXT DEFAULT '[]',
                truth_hash TEXT DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS timeline_entries (
                id TEXT NOT NULL UNIQUE,
                page_id TEXT NOT NULL,
                content TEXT NOT NULL,
                source TEXT DEFAULT 'unknown',
                author TEXT DEFAULT 'system',
                timestamp REAL DEFAULT (unixepoch()),
                confidence REAL DEFAULT 0.5,
                previous_truth_hash TEXT,
                new_truth_hash TEXT
            );

            CREATE TABLE IF NOT EXISTS ingest_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                page_id TEXT,
                source TEXT,
                content_length INTEGER,
                truth_changed INTEGER DEFAULT 0,
                confidence_delta REAL DEFAULT 0,
                ingested_at REAL DEFAULT (unixepoch())
            );

            CREATE INDEX IF NOT EXISTS idx_cp_type ON compiled_pages(entity_type);
            CREATE INDEX IF NOT EXISTS idx_cp_name ON compiled_pages(entity_name);
            CREATE INDEX IF NOT EXISTS idx_te_page ON timeline_entries(page_id);
            CREATE INDEX IF NOT EXISTS idx_te_time ON timeline_entries(timestamp);
            CREATE INDEX IF NOT EXISTS idx_il_page ON ingest_log(page_id);
            CREATE INDEX IF NOT EXISTS idx_il_time ON ingest_log(ingested_at);
            """;
        cmd.ExecuteNonQuery();
    }

    public CompiledTruthPage? GetPage(string pageId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM compiled_pages WHERE page_id = $id";
        cmd.Parameters.AddWithValue("$id", pageId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return ReadPage(reader);
    }

    public List<CompiledTruthPage> ListPages(string? entityType = null, int limit = 50)
    {
        var results = new List<CompiledTruthPage>();
        using var cmd = _conn.CreateCommand();
        if (entityType != null)
        {
            cmd.CommandText = "SELECT * FROM compiled_pages WHERE entity_type = $type ORDER BY updated_at DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$type", entityType);
        }
        else
        {
            cmd.CommandText = "SELECT * FROM compiled_pages ORDER BY updated_at DESC LIMIT $limit";
        }
        cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadPage(reader));
        return results;
    }

    public List<CompiledTruthPage> SearchPages(string query, int limit = 20)
    {
        var results = new List<CompiledTruthPage>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM compiled_pages
            WHERE entity_name LIKE $q OR compiled_truth LIKE $q OR tags LIKE $q
            ORDER BY confidence DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$q", $"%{query}%");
        cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadPage(reader));
        return results;
    }

    public List<TimelineEntry> GetTimeline(string pageId, int limit = 100)
    {
        var results = new List<TimelineEntry>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM timeline_entries WHERE page_id = $pid ORDER BY timestamp DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$pid", pageId);
        cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadTimelineEntry(reader));
        return results;
    }

    public IngestResult Ingest(
        string pageId, string entityName, string newEvidence,
        string source = "unknown", string entityType = "concept",
        double evidenceConfidence = 0.5)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var existing = GetPage(pageId);
        var previousHash = existing?.TruthHash;

        var timelineEntry = new TimelineEntry
        {
            PageId = pageId,
            Content = newEvidence,
            Source = source,
            Timestamp = now,
            Confidence = evidenceConfidence,
            PreviousTruthHash = previousHash
        };

        InsertTimelineEntry(timelineEntry);

        var (newTruth, newConfidence, changed) = CompileTruth(
            existing?.CompiledTruth ?? "", existing?.Confidence ?? 0,
            newEvidence, evidenceConfidence, existing?.EvidenceCount ?? 0);

        var newHash = ComputeHash(newTruth);

        var truthChanged = changed || (existing == null);

        UpsertPage(pageId, entityName, entityType, newTruth, newConfidence,
            (existing?.EvidenceCount ?? 0) + 1,
            (existing?.RevisionCount ?? 0) + (truthChanged ? 1 : 0),
            newHash, now);

        if (truthChanged && timelineEntry.NewTruthHash == null)
        {
            using var updateCmd = _conn.CreateCommand();
            updateCmd.CommandText = "UPDATE timeline_entries SET new_truth_hash = $hash WHERE id = $id";
            updateCmd.Parameters.AddWithValue("$hash", newHash);
            updateCmd.Parameters.AddWithValue("$id", timelineEntry.Id);
            updateCmd.ExecuteNonQuery();
        }

        LogIngest(pageId, source, newEvidence.Length, truthChanged,
            newConfidence - (existing?.Confidence ?? 0), now);

        return new IngestResult
        {
            PageId = pageId,
            TruthChanged = truthChanged,
            NewConfidence = newConfidence,
            TimelineCount = GetTimelineCount(pageId),
            DiffSummary = truthChanged ? $"Truth updated: confidence {newConfidence:F2}" : null
        };
    }

    private static (string truth, double confidence, bool changed) CompileTruth(
        string currentTruth, double currentConfidence,
        string newEvidence, double evidenceConfidence,
        int existingEvidenceCount)
    {
        if (string.IsNullOrEmpty(currentTruth))
            return (newEvidence, evidenceConfidence, true);

        var overlap = ComputeOverlap(currentTruth, newEvidence);

        if (overlap > 0.7)
        {
            var mergedConfidence = Math.Min(1.0,
                currentConfidence * 0.7 + evidenceConfidence * 0.3);
            return (currentTruth, mergedConfidence, false);
        }

        var totalEvidence = existingEvidenceCount + 1;
        var newConfidence = Math.Min(1.0,
            (currentConfidence * existingEvidenceCount + evidenceConfidence) / (totalEvidence));

        var mergedTruth = BuildMergedTruth(currentTruth, newEvidence, overlap);

        return (mergedTruth, newConfidence, true);
    }

    private static string BuildMergedTruth(string current, string newEvidence, double overlap)
    {
        if (overlap < 0.1)
            return current.Length > newEvidence.Length
                ? $"{current}\n\n## 新证据\n{newEvidence[..Math.Min(1000, newEvidence.Length)]}"
                : newEvidence;

        var currentSentences = SplitSentences(current);
        var newSentences = SplitSentences(newEvidence);
        var allSentences = new HashSet<string>(currentSentences);
        allSentences.UnionWith(newSentences);

        var merged = new StringBuilder();
        foreach (var s in allSentences.OrderByDescending(x => x.Length).Take(20))
        {
            if (merged.Length + s.Length > 3000) break;
            merged.AppendLine(s);
        }

        return merged.ToString().TrimEnd();
    }

    private static List<string> SplitSentences(string text) =>
        System.Text.RegularExpressions.Regex.Split(text, @"(?<=[。.!！?？\n])")
            .Select(s => s.Trim())
            .Where(s => s.Length > 5)
            .ToList();

    private static double ComputeOverlap(string a, string b)
    {
        var wa = new HashSet<string>(a.ToLower().Split(new[] { ' ', '\n', '。', '，' }, StringSplitOptions.RemoveEmptyEntries));
        var wb = new HashSet<string>(b.ToLower().Split(new[] { ' ', '\n', '。', '，' }, StringSplitOptions.RemoveEmptyEntries));
        if (wa.Count == 0 || wb.Count == 0) return 0;
        return (double)wa.Intersect(wb).Count() / Math.Max(wa.Count, wb.Count);
    }

    private void UpsertPage(string pageId, string entityName, string entityType,
        string truth, double confidence, int evidenceCount, int revisionCount,
        string hash, double timestamp)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO compiled_pages
                (page_id, entity_type, entity_name, compiled_truth, confidence,
                 evidence_count, revision_count, truth_hash, created_at, updated_at)
            VALUES
                ($pid, $type, $name, $truth, $conf, $ev, $rev, $hash, $ts, $ts)
            ON CONFLICT(page_id) DO UPDATE SET
                compiled_truth = $truth,
                confidence = $conf,
                evidence_count = $ev,
                revision_count = $rev,
                truth_hash = $hash,
                updated_at = $ts
            """;
        cmd.Parameters.AddWithValue("$pid", pageId);
        cmd.Parameters.AddWithValue("$type", entityType);
        cmd.Parameters.AddWithValue("$name", entityName);
        cmd.Parameters.AddWithValue("$truth", truth);
        cmd.Parameters.AddWithValue("$conf", confidence);
        cmd.Parameters.AddWithValue("$ev", evidenceCount);
        cmd.Parameters.AddWithValue("$rev", revisionCount);
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$ts", timestamp);
        cmd.ExecuteNonQuery();
    }

    private void InsertTimelineEntry(TimelineEntry entry)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO timeline_entries (id, page_id, content, source, author, timestamp, confidence, previous_truth_hash, new_truth_hash)
            VALUES ($id, $pid, $content, $source, $author, $ts, $conf, $prev, $new)
            """;
        cmd.Parameters.AddWithValue("$id", entry.Id);
        cmd.Parameters.AddWithValue("$pid", entry.PageId);
        cmd.Parameters.AddWithValue("$content", entry.Content);
        cmd.Parameters.AddWithValue("$source", entry.Source);
        cmd.Parameters.AddWithValue("$author", entry.Author);
        cmd.Parameters.AddWithValue("$ts", entry.Timestamp);
        cmd.Parameters.AddWithValue("$conf", entry.Confidence);
        cmd.Parameters.AddWithValue("$prev", (object?)entry.PreviousTruthHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$new", (object?)entry.NewTruthHash ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private void LogIngest(string pageId, string source, int contentLength,
        bool truthChanged, double confidenceDelta, double timestamp)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ingest_log (page_id, source, content_length, truth_changed, confidence_delta, ingested_at)
            VALUES ($pid, $src, $len, $changed, $delta, $ts)
            """;
        cmd.Parameters.AddWithValue("$pid", pageId);
        cmd.Parameters.AddWithValue("$src", source);
        cmd.Parameters.AddWithValue("$len", contentLength);
        cmd.Parameters.AddWithValue("$changed", truthChanged ? 1 : 0);
        cmd.Parameters.AddWithValue("$delta", confidenceDelta);
        cmd.Parameters.AddWithValue("$ts", timestamp);
        cmd.ExecuteNonQuery();
    }

    private int GetTimelineCount(string pageId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM timeline_entries WHERE page_id = $pid";
        cmd.Parameters.AddWithValue("$pid", pageId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public Dictionary<string, object> GetStats()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM compiled_pages) as pages,
                (SELECT COUNT(*) FROM timeline_entries) as timeline,
                (SELECT COUNT(*) FROM ingest_log) as ingestions,
                (SELECT COUNT(*) FROM ingest_log WHERE truth_changed = 1) as truth_changes
            """;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return new() { ["pages"] = 0, ["timeline"] = 0, ["ingestions"] = 0, ["truth_changes"] = 0 };

        return new()
        {
            ["pages"] = Convert.ToInt32(reader["pages"]),
            ["timeline"] = Convert.ToInt32(reader["timeline"]),
            ["ingestions"] = Convert.ToInt32(reader["ingestions"]),
            ["truth_changes"] = Convert.ToInt32(reader["truth_changes"])
        };
    }

    private static CompiledTruthPage ReadPage(SqliteDataReader reader) => new()
    {
        PageId = reader["page_id"].ToString() ?? "",
        EntityType = reader["entity_type"].ToString() ?? "concept",
        EntityName = reader["entity_name"].ToString() ?? "",
        CompiledTruth = reader["compiled_truth"].ToString() ?? "",
        Confidence = Convert.ToDouble(reader["confidence"]),
        EvidenceCount = Convert.ToInt32(reader["evidence_count"]),
        CreatedAt = Convert.ToDouble(reader["created_at"]),
        UpdatedAt = Convert.ToDouble(reader["updated_at"]),
        RevisionCount = Convert.ToInt32(reader["revision_count"]),
        TruthHash = reader["truth_hash"].ToString() ?? "",
        Tags = DeserializeTags(reader["tags"].ToString() ?? "[]")
    };

    private static TimelineEntry ReadTimelineEntry(SqliteDataReader reader) => new()
    {
        Id = reader["id"].ToString() ?? "",
        PageId = reader["page_id"].ToString() ?? "",
        Content = reader["content"].ToString() ?? "",
        Source = reader["source"].ToString() ?? "unknown",
        Author = reader["author"].ToString() ?? "system",
        Timestamp = Convert.ToDouble(reader["timestamp"]),
        Confidence = Convert.ToDouble(reader["confidence"]),
        PreviousTruthHash = reader["previous_truth_hash"] is DBNull ? null : reader["previous_truth_hash"].ToString(),
        NewTruthHash = reader["new_truth_hash"] is DBNull ? null : reader["new_truth_hash"].ToString()
    };

    private static List<string> DeserializeTags(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch { return new(); }
    }

    private static string ComputeHash(string text)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(bytes)[..12];
    }

    public void Dispose() => _conn?.Dispose();
}
