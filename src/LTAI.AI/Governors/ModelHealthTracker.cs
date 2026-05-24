using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed class ModelHealthTracker
{
    private readonly ConcurrentDictionary<string, ModelHealth> _models = new();
    private readonly ILogger<ModelHealthTracker>? _logger;
    private const int WindowSize = 20;
    private const float UnhealthyThreshold = 0.3f;
    private const float RecoveryThreshold = 0.7f;

    private sealed record ModelHealth
    {
        public readonly Queue<bool> RecentResults = new();
        public bool IsDegraded;
        public int TotalCalls;
        public int TotalFailures;

        public float RecentSuccessRate
        {
            get
            {
                if (RecentResults.Count == 0) return 1.0f;
                return (float)RecentResults.Count(r => r) / RecentResults.Count;
            }
        }

        public void Record(bool success)
        {
            lock (RecentResults)
            {
                RecentResults.Enqueue(success);
                while (RecentResults.Count > WindowSize)
                    RecentResults.Dequeue();
            }
            TotalCalls++;
            if (!success) TotalFailures++;
        }
    }

    public ModelHealthTracker(ILogger<ModelHealthTracker>? logger = null)
    {
        _logger = logger;
    }

    public void RecordSuccess(string modelId)
    {
        var health = _models.GetOrAdd(modelId, _ => new ModelHealth());
        health.Record(true);

        if (health.IsDegraded && health.RecentSuccessRate >= RecoveryThreshold)
        {
            health.IsDegraded = false;
            _logger?.LogInformation("ModelHealth: {Model} recovered (rate={Rate:F2})", modelId, health.RecentSuccessRate);
        }
    }

    public void RecordFailure(string modelId)
    {
        var health = _models.GetOrAdd(modelId, _ => new ModelHealth());
        health.Record(false);

        if (!health.IsDegraded && health.RecentSuccessRate < UnhealthyThreshold && health.RecentResults.Count >= 5)
        {
            health.IsDegraded = false; // start tracking degradation
            _logger?.LogWarning("ModelHealth: {Model} degraded (rate={Rate:F2}, failures={Fails})",
                modelId, health.RecentSuccessRate, health.RecentResults.Count(r => !r));
        }
    }

    public bool IsHealthy(string modelId)
    {
        if (!_models.TryGetValue(modelId, out var health)) return true;
        if (health.RecentResults.Count < 5) return true;
        return health.RecentSuccessRate >= UnhealthyThreshold;
    }

    public float GetHealth(string modelId)
    {
        return _models.TryGetValue(modelId, out var health) ? health.RecentSuccessRate : 1.0f;
    }

    public (int Calls, int Failures) GetStats(string modelId)
    {
        if (_models.TryGetValue(modelId, out var health))
            return (health.TotalCalls, health.TotalFailures);
        return (0, 0);
    }
}
