// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  RoutingSkillStore — file-backed routing skill memory
//
//  Persists per-specialist routing statistics to a JSON file in
//  .livingtree/routing-skills.json. Loads on startup, saves on
//  every N records (batched write).
//
//  Thread-safe: ConcurrentDictionary + periodic flush.
// ═══════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using System.Text.Json;
using LTAI.Agent.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Learning;

/// <summary>
/// File-backed IRoutingSkillStore. Persists routing skill data
/// to .livingtree/routing-skills.json. Batched writes reduce I/O.
/// </summary>
public sealed class RoutingSkillStore : IRoutingSkillStore, IDisposable
{
    private readonly ConcurrentDictionary<string, RoutingSkillStat> _stats = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _filePath;
    private readonly ILogger<RoutingSkillStore> _logger;
    private readonly int _flushInterval;
    private int _pendingWrites;
    private readonly object _flushLock = new();
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly QueryClassifier? _queryClassifier;

    public RoutingSkillStore(
        string dataDir,
        ILogger<RoutingSkillStore>? logger = null,
        int flushInterval = 10,
        QueryClassifier? queryClassifier = null)
    {
        _filePath = Path.Combine(dataDir, "routing-skills.json");
        _logger = logger ?? NullLogger<RoutingSkillStore>.Instance;
        _flushInterval = flushInterval;
        _queryClassifier = queryClassifier;

        LoadFromDisk();
    }

    /// <inheritdoc />
    public void RecordOutcome(string query, string specialist, bool success, long latencyMs, int tokensUsed)
    {
        var stat = _stats.GetOrAdd(specialist, name => new RoutingSkillStat { Specialist = name });
        Interlocked.Increment(ref stat.TotalCalls);
        if (success) Interlocked.Increment(ref stat.Successes);
        Interlocked.Add(ref stat.TotalLatencyMs, latencyMs);
        Interlocked.Add(ref stat.TotalTokens, tokensUsed);
        stat.LastUsed = DateTime.UtcNow;

        var queryType = DetectQueryType(query);
        lock (stat.QueryTypeCounts)
        {
            stat.QueryTypeCounts[queryType] = stat.QueryTypeCounts.TryGetValue(queryType, out var c) ? c + 1 : 1;
        }

        // Periodic flush
        var pending = Interlocked.Increment(ref _pendingWrites);
        if (pending >= _flushInterval)
            FlushToDisk();
    }

    /// <inheritdoc />
    public double GetSuccessRate(string specialist)
    {
        return _stats.TryGetValue(specialist, out var stat) ? stat.SuccessRate : 0.5;
    }

    /// <inheritdoc />
    public double GetConfidenceBoost(string specialist)
    {
        if (!_stats.TryGetValue(specialist, out var stat) || stat.TotalCalls < 3)
            return 0; // not enough data

        var rate = stat.SuccessRate;
        // Boost range: [-0.3, +0.3]
        // rate 0.9 → +0.3, rate 0.5 → 0, rate 0.2 → -0.3
        return Math.Clamp((rate - 0.5) * 1.5, -0.3, 0.3);
    }

    /// <inheritdoc />
    public IReadOnlyList<(string specialist, double score)> GetTopForQueryType(string queryType, int topK = 3)
    {
        return _stats.Values
            .Where(s => s.QueryTypeCounts.ContainsKey(queryType))
            .Select(s => (s.Specialist, Score: s.SuccessRate * Math.Min(s.TotalCalls, 50) / 50.0))
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc />
    public string DetectQueryType(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "unknown";

        var lower = query.ToLowerInvariant();

        // Code-related
        if (lower.Contains("code") || lower.Contains("write") || lower.Contains("function")
            || lower.Contains("class") || lower.Contains("implement") || lower.Contains("debug"))
            return "code";

        // Data-related
        if (lower.Contains("data") || lower.Contains("query") || lower.Contains("sql")
            || lower.Contains("database") || lower.Contains("analyze") || lower.Contains("统计"))
            return "data";

        // Math-related
        if (lower.Contains("math") || lower.Contains("calculate") || lower.Contains("equation")
            || lower.Contains("formula") || lower.Contains("compute") || lower.Contains("数学"))
            return "math";

        // Writing-related
        if (lower.Contains("write") || lower.Contains("essay") || lower.Contains("article")
            || lower.Contains("document") || lower.Contains("report") || lower.Contains("写作"))
            return "writing";

        // System-related
        if (lower.Contains("system") || lower.Contains("config") || lower.Contains("terminal")
            || lower.Contains("shell") || lower.Contains("install") || lower.Contains("命令"))
            return "system";

        // Greeting — delegate to unified QueryClassifier
        if ((_queryClassifier?.IsGreetingOnly(query) ?? Memory.QueryClassifier.IsGreetingOnlyStatic(query))
            || lower.Length <= 20 && (lower is "hi" or "hello" or "hey" or "你好"
                || lower.StartsWith("hi ") || lower.StartsWith("hello ") || lower.StartsWith("你好 ")))
            return "greeting";

        return "general";
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, RoutingSkillStat> GetAllStats()
    {
        return new Dictionary<string, RoutingSkillStat>(_stats, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public void Reset()
    {
        _stats.Clear();
        if (File.Exists(_filePath))
        {
            try { File.Delete(_filePath); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete routing skills file"); }
        }
    }

    /// <summary>Explicitly flush to disk.</summary>
    public void FlushToDisk()
    {
        lock (_flushLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var snapshot = _stats.ToDictionary(
                    kv => kv.Key,
                    kv => new RoutingSkillStat
                    {
                        Specialist = kv.Value.Specialist,
                        TotalCalls = kv.Value.TotalCalls,
                        Successes = kv.Value.Successes,
                        TotalLatencyMs = kv.Value.TotalLatencyMs,
                        TotalTokens = kv.Value.TotalTokens,
                        LastUsed = kv.Value.LastUsed,
                    });

                var json = JsonSerializer.Serialize(snapshot, JsonOpts);
                File.WriteAllText(_filePath, json);
                Interlocked.Exchange(ref _pendingWrites, 0);
                _logger.LogDebug("RoutingSkillStore: flushed {Count} stats to disk", snapshot.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RoutingSkillStore: failed to flush");
            }
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, RoutingSkillStat>>(json, JsonOpts);
            if (loaded != null)
            {
                foreach (var (name, stat) in loaded)
                    _stats[name] = stat;
                _logger.LogInformation("RoutingSkillStore: loaded {Count} skill records", _stats.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RoutingSkillStore: failed to load from {Path}", _filePath);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        FlushToDisk();
    }
}
