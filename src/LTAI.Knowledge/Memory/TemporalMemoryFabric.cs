using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace LTAI.Knowledge.Memory;

public sealed record MemoryEvent
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string SessionId { get; init; } = "";
    public string AgentName { get; init; } = "";
    public string UserQuery { get; init; } = "";
    public string? AgentResponse { get; init; }
    public string? FilePath { get; init; }
    public string? KnowledgeKey { get; init; }
    public string? GraphTriplet { get; init; }
    public List<string> ToolCalls { get; init; } = new();
    public List<float> VectorEmbedding { get; init; } = new();
    public double Importance { get; init; } = 0.5;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public Dictionary<string, string> Metadata { get; init; } = new();
}

public sealed record MemoryQueryResult
{
    public string Id { get; init; } = "";
    public string Source { get; init; } = ""; // fts5 / vector / graph
    public string Content { get; init; } = "";
    public double Score { get; init; }
    public DateTime Timestamp { get; init; }
    public string? FilePath { get; init; }
    public string? GraphTriplet { get; init; }
}

public sealed class TemporalMemoryFabric
{
    private readonly ILogger<TemporalMemoryFabric> _logger;
    private readonly ConcurrentDictionary<string, MemoryEvent> _events = new();
    private readonly ConcurrentDictionary<string, List<string>> _timeline = new();
    private readonly ConcurrentDictionary<string, List<string>> _sessionIndex = new();
    private readonly ConcurrentDictionary<string, List<string>> _fileIndex = new();
    private readonly object _lock = new();
    private const int MaxEvents = 10000;

    // Forward references to external stores (set after construction)
    public Func<string, Task<List<(string id, string content, double score)>>>? FTS5SearchAsync { get; set; }
    public Func<float[], int, Task<List<(string id, double score)>>>? VectorSearchAsync { get; set; }
    public Func<string, Task<List<(string subject, string relation, string obj)>>>? GraphQueryAsync { get; set; }

    public TemporalMemoryFabric(ILogger<TemporalMemoryFabric> logger)
    {
        _logger = logger;
    }

    public void RecordEvent(MemoryEvent evt)
    {
        var key = $"{evt.Timestamp:yyyy-MM-dd}";
        _events[evt.Id] = evt;

        _timeline.AddOrUpdate(key,
            _ => new List<string> { evt.Id },
            (_, list) => { list.Add(evt.Id); return list; });

        _sessionIndex.AddOrUpdate(evt.SessionId,
            _ => new List<string> { evt.Id },
            (_, list) => { list.Add(evt.Id); return list; });

        if (evt.FilePath != null)
        {
            var normalizedPath = evt.FilePath.ToLowerInvariant();
            _fileIndex.AddOrUpdate(normalizedPath,
                _ => new List<string> { evt.Id },
                (_, list) => { list.Add(evt.Id); return list; });
        }

        lock (_lock)
        {
            if (_events.Count > MaxEvents)
            {
                var oldest = _events.Values
                    .OrderBy(e => e.Timestamp)
                    .Take(500)
                    .Select(e => e.Id)
                    .ToList();
                foreach (var id in oldest) _events.TryRemove(id, out _);
            }
        }

        _logger.LogDebug("TemporalMemory: Recorded event {Id} for session {Session}",
            evt.Id, evt.SessionId);
    }

    public async Task<List<MemoryQueryResult>> QueryAsync(
        string query, TimeSpan? timeWindow = null, string? filePath = null, int topK = 10)
    {
        var results = new List<MemoryQueryResult>();

        // 1. FTS5 exact search
        if (FTS5SearchAsync != null)
        {
            var ftsResults = await FTS5SearchAsync(query).ConfigureAwait(false);
            foreach (var (id, content, score) in ftsResults.Take(topK))
            {
                if (_events.TryGetValue(id, out var evt))
                {
                    if (IsInTimeWindow(evt, timeWindow) && IsInFilePath(evt, filePath))
                        results.Add(new MemoryQueryResult { Id = id, Source = "fts5", Content = content, Score = score, Timestamp = evt.Timestamp, FilePath = evt.FilePath });
                }
            }
        }

        // 2. Vector semantic search using query embedding
        if (VectorSearchAsync != null)
        {
            var queryEmbedding = ComputeSimpleEmbedding(query);
            var vecResults = await VectorSearchAsync(queryEmbedding, topK).ConfigureAwait(false);
            foreach (var (id, score) in vecResults)
            {
                if (_events.TryGetValue(id, out var evt) && results.All(r => r.Id != id))
                {
                    if (IsInTimeWindow(evt, timeWindow) && IsInFilePath(evt, filePath))
                        results.Add(new MemoryQueryResult { Id = id, Source = "vector", Content = evt.UserQuery, Score = score, Timestamp = evt.Timestamp });
                }
            }
        }

        // 3. Knowledge graph traverse
        if (GraphQueryAsync != null)
        {
            var graphResults = await GraphQueryAsync(query).ConfigureAwait(false);
            foreach (var (subject, relation, obj) in graphResults.Take(topK))
            {
                var triplet = $"{subject} {relation} {obj}";
                var matchedEvents = _events.Values
                    .Where(e => e.GraphTriplet != null &&
                                e.GraphTriplet.Contains(subject, StringComparison.OrdinalIgnoreCase))
                    .Take(3);

                foreach (var evt in matchedEvents)
                {
                    if (results.All(r => r.Id != evt.Id) && IsInTimeWindow(evt, timeWindow))
                        results.Add(new MemoryQueryResult { Id = evt.Id, Source = "graph", Content = triplet, Score = 0.7, Timestamp = evt.Timestamp, GraphTriplet = triplet });
                }
            }
        }

        return results.OrderByDescending(r => r.Score).Take(topK).ToList();
    }

    public List<MemoryEvent> GetSessionHistory(string sessionId, int count = 50)
    {
        if (!_sessionIndex.TryGetValue(sessionId, out var eventIds))
            return new List<MemoryEvent>();

        return eventIds
            .Select(id => _events.GetValueOrDefault(id))
            .Where(e => e != null)
            .OrderByDescending(e => e!.Timestamp)
            .Take(count)
            .Select(e => e!)
            .ToList();
    }

    public List<MemoryEvent> GetFileReferences(string filePath, int count = 20)
    {
        var normalizedPath = filePath.ToLowerInvariant();
        var matching = new List<string>();

        foreach (var (path, eventIds) in _fileIndex)
        {
            if (path.Contains(normalizedPath))
                matching.AddRange(eventIds);
        }

        return matching
            .Distinct()
            .Select(id => _events.GetValueOrDefault(id))
            .Where(e => e != null)
            .OrderByDescending(e => e!.Timestamp)
            .Take(count)
            .Select(e => e!)
            .ToList();
    }

    public List<MemoryEvent> QueryTimeRange(DateTime from, DateTime to, int count = 100)
    {
        return _events.Values
            .Where(e => e.Timestamp >= from && e.Timestamp <= to)
            .OrderByDescending(e => e.Timestamp)
            .Take(count)
            .ToList();
    }

    public Dictionary<string, object> GetStats()
    {
        lock (_lock)
        {
            return new()
            {
                ["total_events"] = _events.Count,
                ["timeline_days"] = _timeline.Count,
                ["sessions"] = _sessionIndex.Count,
                ["files_tracked"] = _fileIndex.Count,
                ["avg_importance"] = _events.Values.Count > 0
                    ? _events.Values.Average(e => e.Importance) : 0,
                ["recent_events"] = _events.Values
                    .OrderByDescending(e => e.Timestamp)
                    .Take(5)
                    .Select(e => new
                    {
                        e.Id, e.AgentName,
                        query = e.UserQuery[..Math.Min(e.UserQuery.Length, 100)],
                        e.Importance, e.Timestamp
                    }).ToList()
            };
        }
    }

    private static bool IsInTimeWindow(MemoryEvent evt, TimeSpan? window) =>
        window == null || (DateTime.UtcNow - evt.Timestamp) <= window.Value;

    private static bool IsInFilePath(MemoryEvent evt, string? path)
    {
        if (path == null) return true;
        if (evt.FilePath == null) return false;
        return evt.FilePath.Contains(path, StringComparison.OrdinalIgnoreCase);
    }

    private static float[] ComputeSimpleEmbedding(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var vector = new float[32];
        for (int i = 0; i < 32 && i * 8 < hash.Length; i++)
        {
            long val = 0;
            for (int j = 0; j < 8 && i * 8 + j < hash.Length; j++)
                val = (val << 8) | hash[i * 8 + j];
            vector[i] = (float)(val / (double)long.MaxValue);
        }
        var norm = Math.Sqrt(vector.Sum(v => v * v));
        if (norm > 0)
            for (int i = 0; i < vector.Length; i++)
                vector[i] /= (float)norm;
        return vector;
    }
}
