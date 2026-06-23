using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Memory;

public sealed class PredictiveOffloadTracker
{
    private readonly ConcurrentDictionary<string, PredictionStats> _stats = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<PredictiveOffloadTracker> _logger;

    public PredictiveOffloadTracker(ILogger<PredictiveOffloadTracker>? logger = null)
    {
        _logger = logger ?? NullLogger<PredictiveOffloadTracker>.Instance;
    }

    public void RecordResult(string toolName, int resultLength)
    {
        _stats.AddOrUpdate(toolName,
            _ => new PredictionStats(1, resultLength, resultLength),
            (_, existing) => existing with
            {
                Count = existing.Count + 1,
                TotalSize = existing.TotalSize + resultLength,
                MaxSize = Math.Max(existing.MaxSize, resultLength),
            });
    }

    public bool ShouldPreOffload(string toolName, int currentResultLength)
    {
        if (!_stats.TryGetValue(toolName, out var stats)) return false;
        if (stats.Count < 3) return false;
        var avg = stats.AverageSize;
        return currentResultLength > avg * 0.8 && avg > 2000;
    }

    public PredictionStats? GetStats(string toolName) =>
        _stats.TryGetValue(toolName, out var s) ? s : null;

    public IReadOnlyDictionary<string, PredictionStats> Snapshot() =>
        new Dictionary<string, PredictionStats>(_stats);
}

public sealed record PredictionStats(int Count, int TotalSize, int MaxSize)
{
    public double AverageSize => Count > 0 ? (double)TotalSize / Count : 0;
}
