using System.Text.Json;

namespace LTAI.Core.System;

/// <summary>
/// Append-only audit log service. All security-relevant decisions
/// (routing, token validation, gate blocks, policy violations) are
/// recorded here for non-repudiation.
/// </summary>
public sealed class AuditLogService : IDisposable
{
    private readonly string _logPath;
    private readonly StreamWriter _writer;
    private readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private long _entryCount;
    private readonly object _lock = new();

    public AuditLogService(string? baseDir = null)
    {
        var dir = baseDir != null
            ? Path.Combine(baseDir, ".livingtree", "audit")
            : Path.Combine(AppContext.BaseDirectory, ".livingtree", "audit");
        Directory.CreateDirectory(dir);

        _logPath = Path.Combine(dir, "audit.jsonl");
        _writer = new StreamWriter(_logPath, append: true) { AutoFlush = true };
        _entryCount = new FileInfo(_logPath).Exists ? new FileInfo(_logPath).Length : 0;
    }

    /// <summary>Record an audit entry. Thread-safe, append-only.</summary>
    public void Record(string source, string eventType, string detail,
        string? subject = null, double? riskScore = null, string? result = null)
    {
        var entry = new Dictionary<string, object?>
        {
            ["timestamp"] = DateTime.UtcNow.ToString("O"),
            ["source"] = source,
            ["event"] = eventType,
            ["detail"] = detail,
            ["subject"] = subject,
            ["risk_score"] = riskScore,
            ["result"] = result
        };

        var json = JsonSerializer.Serialize(entry, _jsonOpts);

        lock (_lock)
        {
            _writer.WriteLine(json);
            Interlocked.Increment(ref _entryCount);
        }
    }

    /// <summary>Read back all audit entries (read-only replay).</summary>
    public List<Dictionary<string, object?>> Replay(int? maxEntries = null)
    {
        var results = new List<Dictionary<string, object?>>();
        var lines = File.ReadAllLines(_logPath);
        var take = maxEntries ?? lines.Length;

        foreach (var line in lines.TakeLast(take))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<Dictionary<string, object?>>(line);
                if (entry != null) results.Add(entry);
            }
            catch { /* skip corrupt lines */ }
        }

        return results;
    }

    /// <summary>Get number of entries recorded this session.</summary>
    public long EntryCount => _entryCount;

    /// <summary>Get the log file path.</summary>
    public string LogPath => _logPath;

    public void Dispose()
    {
        _writer?.Dispose();
    }
}
