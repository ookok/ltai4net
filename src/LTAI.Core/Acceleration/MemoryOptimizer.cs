using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Acceleration;

public record MemoryStats(
    long TotalMb,
    long UsedMb,
    long AvailableMb,
    double Percent,
    long SwapUsedMb);

public sealed class CacheEntry
{
    public string Key { get; set; }
    public string Response { get; set; }
    public DateTime CreatedAt { get; set; }
    public int TtlSeconds { get; set; }
    public int Hits { get; set; }
    public int TokensSaved { get; set; }

    public CacheEntry(string key, string response, DateTime createdAt, int ttlSeconds, int hits, int tokensSaved)
    {
        Key = key;
        Response = response;
        CreatedAt = createdAt;
        TtlSeconds = ttlSeconds;
        Hits = hits;
        TokensSaved = tokensSaved;
    }
}

public sealed class ResponseCache
{
    private static readonly Lazy<ResponseCache> _instance = new(() => new ResponseCache());
    public static ResponseCache Instance => _instance.Value;

    private readonly Dictionary<string, LinkedListNode<(string Key, CacheEntry Entry)>> _index = new();
    private readonly LinkedList<(string Key, CacheEntry Entry)> _order = new();
    private readonly object _lock = new();
    private readonly ILogger<ResponseCache> _logger;
    private const int MaxEntries = 500;
    private long _totalHits;

    public ResponseCache() : this(NullLogger<ResponseCache>.Instance) { }

    public ResponseCache(ILogger<ResponseCache> logger)
    {
        _logger = logger ?? NullLogger<ResponseCache>.Instance;
    }

    public IReadOnlyDictionary<string, CacheEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _order.ToDictionary(n => n.Key, n => n.Entry);
            }
        }
    }

    public static string MakeKey(string query, string model)
    {
        var input = $"{query}|{model}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hash)[..32];
    }

    public string? Get(string query, string model)
    {
        var key = MakeKey(query, model);
        lock (_lock)
        {
            if (!_index.TryGetValue(key, out var node))
                return null;

            var entry = node.Value.Entry;
            var age = (DateTime.UtcNow - entry.CreatedAt).TotalSeconds;
            if (age > entry.TtlSeconds)
            {
                _order.Remove(node);
                _index.Remove(key);
                _logger.LogDebug("Cache entry expired: {Key}", key);
                return null;
            }

            entry.Hits++;
            Interlocked.Increment(ref _totalHits);
            _order.Remove(node);
            _order.AddFirst(node);
            _logger.LogDebug("Cache hit: {Key}, Hits: {Hits}", key, entry.Hits);
            return entry.Response;
        }
    }

    public void Set(string query, string response, string model, int ttlSeconds = 300)
    {
        var key = MakeKey(query, model);
        lock (_lock)
        {
            if (_index.TryGetValue(key, out var existing))
                _order.Remove(existing);

            while (_index.Count >= MaxEntries && _order.Last != null)
            {
                _index.Remove(_order.Last.Value.Key);
                _order.RemoveLast();
            }

            var entry = new CacheEntry(key, response, DateTime.UtcNow, ttlSeconds, 0, 0);
            var node = new LinkedListNode<(string, CacheEntry)>((key, entry));
            _index[key] = node;
            _order.AddFirst(node);
            _logger.LogDebug("Cached response: {Key}, TTL: {TtlSeconds}s", key, ttlSeconds);
        }
    }

    public void Invalidate(string? query = null, string? model = null)
    {
        if (query == null && model == null)
        {
            lock (_lock)
            {
                _index.Clear();
                _order.Clear();
            }
            _logger.LogInformation("Invalidated all cache entries");
            return;
        }

        if (query != null)
        {
            var key = MakeKey(query, model ?? "");
            lock (_lock)
            {
                if (_index.TryGetValue(key, out var node))
                {
                    _order.Remove(node);
                    _index.Remove(key);
                    _logger.LogInformation("Invalidated cache entry: {Key}", key);
                }
            }
        }
    }

    public double HitRate
    {
        get
        {
            var totalHits = Interlocked.Read(ref _totalHits);
            int count;
            lock (_lock) { count = _index.Count; }
            return count > 0 ? (double)totalHits / count : 0.0;
        }
    }

    public Dictionary<string, object> Stats()
    {
        lock (_lock)
        {
            return new Dictionary<string, object>
            {
                ["entry_count"] = _index.Count,
                ["max_entries"] = MaxEntries,
                ["total_hits"] = Interlocked.Read(ref _totalHits),
                ["hit_rate"] = HitRate
            };
        }
    }
}

public static class TokenCompressor
{
    private static readonly Regex s_timestampPattern = new(
        @"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(\.\d+)?",
        RegexOptions.Compiled);
    private static readonly Regex s_uuidPattern = new(
        @"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex s_numberPattern = new(
        @"\b\d+(\.\d+)?\b",
        RegexOptions.Compiled);
    public static string CompressGitDiff(string text)
    {
        var lines = text.Split('\n');
        var result = new List<string>();
        int contextCount = 0;
        bool inHunk = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("diff ") || line.StartsWith("index ") ||
                line.StartsWith("--- ") || line.StartsWith("+++ ") ||
                line.StartsWith("@@ "))
            {
                result.Add(line);
                inHunk = true;
                contextCount = 0;
                continue;
            }

            if (inHunk && line.StartsWith(" "))
            {
                if (contextCount < 2)
                {
                    result.Add(line);
                    contextCount++;
                }
                else if (result.Count > 0 && !result[^1].StartsWith("..."))
                {
                    result.Add("...");
                }
            }
            else
            {
                result.Add(line);
                contextCount = 0;
            }
        }

        return string.Join("\n", result);
    }

    public static string CompressGrep(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var grouped = new Dictionary<string, List<string>>();

        foreach (var line in lines)
        {
            var colonIdx = line.IndexOf(':');
            if (colonIdx == -1)
            {
                if (!grouped.ContainsKey(""))
                    grouped[""] = new List<string>();
                grouped[""].Add(line);
                continue;
            }

            var file = line[..colonIdx];
            if (!grouped.ContainsKey(file))
                grouped[file] = new List<string>();
            grouped[file].Add(line[(colonIdx + 1)..]);
        }

        var result = new List<string>();
        foreach (var kvp in grouped)
        {
            if (string.IsNullOrEmpty(kvp.Key))
            {
                result.AddRange(kvp.Value);
            }
            else
            {
                var sample = kvp.Value.Take(3).Select(v => v.Trim()).ToList();
                result.Add($"{kvp.Key}: {string.Join(" | ", sample)}");
                if (kvp.Value.Count > 3)
                    result.Add($"  ... and {kvp.Value.Count - 3} more matches in {kvp.Key}");
            }
        }

        return string.Join("\n", result);
    }

    public static string CompressLog(string text)
    {
        var lines = text.Split('\n');
        var result = new List<string>();
        var seen = new HashSet<string>();

        foreach (var line in lines)
        {
            var normalized = s_timestampPattern.Replace(line, "[TS]");
            normalized = s_uuidPattern.Replace(normalized, "[UUID]");
            normalized = s_numberPattern.Replace(normalized, "[N]");
            normalized = normalized.Trim();

            if (seen.Add(normalized))
                result.Add(line);
        }

        return string.Join("\n", result);
    }

    public static string CompressStacktrace(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();

        var errorLineIdx = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("Error") || lines[i].Contains("Exception"))
            {
                errorLineIdx = i;
                break;
            }
        }

        if (errorLineIdx >= 0)
            result.Add(lines[errorLineIdx]);

        var frameLines = lines.Where(l => l.TrimStart().StartsWith("at ")).ToList();
        if (frameLines.Count > 0)
        {
            result.Add(frameLines[0]);
            if (frameLines.Count > 4)
            {
                result.Add($"  ... {frameLines.Count - 4} frames omitted ...");
                result.AddRange(frameLines.TakeLast(3));
            }
            else if (frameLines.Count > 1)
            {
                result.AddRange(frameLines.Skip(1));
            }
        }

        return string.Join("\n", result);
    }

    public static (string Compressed, string Filter, double SavedPct) Compress(string content)
    {
        var (filter, compressed) = AutoDetect(content) switch
        {
            "diff" => ("diff", CompressGitDiff(content)),
            "grep" => ("grep", CompressGrep(content)),
            "log" => ("log", CompressLog(content)),
            "stacktrace" => ("stacktrace", CompressStacktrace(content)),
            _ => ("none", content)
        };

        var originalLen = Math.Max(content.Length, 1);
        var savedPct = Math.Round((1.0 - (double)compressed.Length / originalLen) * 100.0, 1);
        return (compressed, filter, savedPct);
    }

    public static string AutoDetect(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "none";

        if (content.StartsWith("diff ") || content.StartsWith("@@ "))
            return "diff";

        if (content.Contains(":") && !content.Contains("\n") != content.Contains(":"))
        {
            var firstLine = content.Split('\n')[0];
            if (Regex.IsMatch(firstLine, @"^[^:]+:\d+:"))
                return "grep";
        }

        if (Regex.IsMatch(content, @"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}"))
            return "log";

        if (content.Contains("Exception") || content.Contains("Error") ||
            content.Contains("at ") && content.Contains(".cs:") ||
            content.Contains("at ") && content.Contains(".py:"))
            return "stacktrace";

        return "none";
    }
}

public static class JitAccel
{
    public static double[] CosineSimilarityBatch(double[] query, List<double[]> docs)
    {
        if (query == null || query.Length == 0 || docs == null || docs.Count == 0)
            return Array.Empty<double>();

        var queryNorm = Math.Sqrt(query.Sum(v => v * v));
        if (queryNorm == 0) return new double[docs.Count];

        var normalizedQuery = query.Select(v => v / queryNorm).ToArray();
        var scores = new double[docs.Count];

        Parallel.For(0, docs.Count, i =>
        {
            var doc = docs[i];
            if (doc.Length != query.Length)
            {
                scores[i] = 0;
                return;
            }

            var docNorm = Math.Sqrt(doc.Sum(v => v * v));
            if (docNorm == 0)
            {
                scores[i] = 0;
                return;
            }

            var dotProduct = 0.0;
            for (int j = 0; j < normalizedQuery.Length; j++)
                dotProduct += normalizedQuery[j] * doc[j] / docNorm;
            scores[i] = dotProduct;
        });

        return scores;
    }

    public static Dictionary<string, object> JitStatus()
    {
        return new Dictionary<string, object>
        {
            ["enabled"] = true,
            ["available"] = true,
            ["simd_supported"] = global::System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated,
            ["vector256_supported"] = global::System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated
        };
    }
}
