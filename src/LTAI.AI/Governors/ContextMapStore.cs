using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed record ContextEntry
{
    public string Key { get; init; } = "";
    public string Value { get; set; } = "";
    public float Confidence { get; set; } = 0.5f;
    public int UseCount { get; set; }
    public DateTime LastUsed { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public double Priority => (Confidence * 0.5 + Math.Min(UseCount / 10.0, 0.3) + RecencyScore * 0.2);

    private double RecencyScore => Math.Exp(-(DateTime.UtcNow - LastUsed).TotalHours / 24.0);
}

public sealed class ContextMapStore
{
    private readonly ConcurrentDictionary<string, ContextEntry> _entries = new();
    private readonly int _maxTokens;
    private readonly ILogger<ContextMapStore> _logger;
    private readonly string _persistPath;

    public IReadOnlyDictionary<string, ContextEntry> Entries => _entries;
    public int EntryCount => _entries.Count;

    public ContextMapStore(ILogger<ContextMapStore> logger, int maxTokens = 200, string? persistPath = null)
    {
        _logger = logger;
        _maxTokens = maxTokens;
        _persistPath = persistPath ?? Path.Combine(AppContext.BaseDirectory, ".livingtree", "context_map.json");
        Load();
    }

    public bool Upsert(string key, string value, float confidence)
    {
        var entry = new ContextEntry { Key = key, Value = value, Confidence = confidence };
        var added = _entries.AddOrUpdate(key,
            _ => { entry.UseCount = 1; return entry; },
            (_, existing) =>
            {
                existing.Value = value;
                existing.Confidence = confidence;
                existing.UseCount++;
                existing.LastUsed = DateTime.UtcNow;
                return existing;
            }) != null;

        Evict();
        return added;
    }

    public void RecordUse(string key)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            entry.UseCount++;
            entry.LastUsed = DateTime.UtcNow;
        }
    }

    public string? Get(string key)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            entry.LastUsed = DateTime.UtcNow;
            return entry.Value;
        }
        return null;
    }

    public string BuildContextMap()
    {
        var entries = _entries.Values
            .OrderByDescending(e => e.Priority)
            .ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[Context Map — persistent orientation knowledge]");

        foreach (var e in entries)
        {
            if (e.Confidence < 0.3f) continue;
            sb.AppendLine($"- {e.Key}: {e.Value}");
        }

        var map = sb.ToString();
        var tokenBudget = _maxTokens * 4;
        if (map.Length > tokenBudget)
        {
            var lines = map.Split('\n').ToList();
            while (string.Join('\n', lines).Length > tokenBudget && lines.Count > 1)
                lines.RemoveAt(lines.Count - 1);
            map = string.Join('\n', lines);
        }

        return map;
    }

    public void Evict()
    {
        var entries = _entries.Values.OrderByDescending(e => e.Priority).ToList();
        var currentTokens = entries.Sum(e => (e.Key.Length + e.Value.Length) / 4);

        var toEvict = entries
            .OrderBy(e => e.Priority)
            .Select(e => e.Key);

        foreach (var key in toEvict)
        {
            if (currentTokens <= _maxTokens) break;
            if (_entries.TryRemove(key, out var removed))
            {
                currentTokens -= (removed.Key.Length + removed.Value.Length) / 4;
                _logger.LogDebug("ContextMap evicted '{Key}' (priority={Pri:F2})", key, removed.Priority);
            }
        }
    }

    public void Distill(string query, string response, string domain, float confidence,
        List<string>? toolSequence = null, List<string>? entities = null)
    {
        if (entities != null)
        {
            foreach (var entity in entities.Take(5))
                Upsert($"entity:{entity}", $"Seen in {domain} context", confidence * 0.7f);
        }

        if (toolSequence != null && toolSequence.Count > 0)
        {
            var pattern = string.Join("→", toolSequence);
            Upsert($"tool_pattern:{domain}", pattern, confidence);
        }

        if (domain != "general")
        {
            var domainKey = $"domain:{domain}";
            if (_entries.TryGetValue(domainKey, out var existing))
            {
                var newConf = Math.Min(1f, existing.Confidence + 0.05f);
                Upsert(domainKey, $"Familiar domain ({existing.UseCount + 1} queries)", newConf);
            }
            else
            {
                Upsert(domainKey, $"Emerging domain (1 query)", 0.3f);
                Upsert($"domain:{domain}:summary",
                    $"Query example: {query[..Math.Min(query.Length, 60)]}", confidence * 0.5f);
            }
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_persistPath))
            {
                var json = File.ReadAllText(_persistPath);
                var entries = JsonSerializer.Deserialize<List<ContextEntry>>(json);
                if (entries != null)
                {
                    foreach (var e in entries)
                        _entries.TryAdd(e.Key, e);
                    _logger.LogInformation("ContextMap loaded: {Count} entries from {Path}", _entries.Count, _persistPath);
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "ContextMap load failed"); }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_persistPath);
            if (dir != null) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_entries.Values.ToList());
            File.WriteAllText(_persistPath, json);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "ContextMap save failed"); }
    }
}
