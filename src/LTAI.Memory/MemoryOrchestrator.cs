using LTAI.Memory.Models;

namespace LTAI.Memory;

public class MemoryOrchestrator
{
    private static MemoryOrchestrator? _instance;
    private static readonly object _instanceLock = new();

    private readonly Lazy<MemPOOptimizer> _mempo;
    private readonly Lazy<EmotionalMemoryStore> _emotional;

    private int _totalProcessed;
    private int _totalEvolved;
    private double _lastProcessTime;

    private MemoryOrchestrator()
    {
        _mempo = new Lazy<MemPOOptimizer>(() => MemPOOptimizer.GetMempoOptimizer());
        _emotional = new Lazy<EmotionalMemoryStore>(() => EmotionalMemoryStore.Instance);
    }

    public static MemoryOrchestrator GetMemoryOrchestrator()
    {
        if (_instance == null)
        {
            lock (_instanceLock)
            {
                _instance ??= new MemoryOrchestrator();
            }
        }
        return _instance;
    }

    public async Task<Dictionary<string, object>> ProcessMemory(
        string content, string taskId, float successRate, object? ctx = null)
    {
        var startTime = DateTime.UtcNow;

        if (_mempo.Value == null)
        {
            return new Dictionary<string, object>
            {
                ["processed"] = false,
                ["evolved"] = false,
                ["emotional_stored"] = false,
                ["error"] = "MemPO not ready"
            };
        }

        _mempo.Value.AddMemory(content);
        _mempo.Value.LogAccess(content.GetHashCode().ToString("x")[..8], taskId);

        OptimizationStats optStats;
        if (successRate >= 0.5f)
        {
            _mempo.Value.OnTaskComplete(taskId, successRate, content);
            optStats = _mempo.Value.Optimize(true);
            _totalEvolved++;
        }
        else
        {
            _mempo.Value.OnTaskFail(taskId);
            optStats = _mempo.Value.Optimize(false);
        }

        var emotionalId = _emotional.Value.Store(content, EmotionalMemoryUtility.DetectEmotion(content));

        _totalProcessed++;
        _lastProcessTime = (DateTime.UtcNow - startTime).TotalMilliseconds;

        return new Dictionary<string, object>
        {
            ["processed"] = true,
            ["evolved"] = successRate >= 0.5f,
            ["emotional_stored"] = !string.IsNullOrEmpty(emotionalId),
            ["success_rate"] = successRate,
            ["optimization_stats"] = optStats,
            ["emotional_id"] = emotionalId
        };
    }

    public Dictionary<string, object> Stats()
    {
        var mempoStats = _mempo.IsValueCreated ? _mempo.Value.GetStats() : new Dictionary<string, object>();
        var emotionalStats = _emotional.IsValueCreated ? _emotional.Value.Stats() : new Dictionary<string, object>();

        return new Dictionary<string, object>
        {
            ["total_processed"] = _totalProcessed,
            ["total_evolved"] = _totalEvolved,
            ["last_process_time_ms"] = _lastProcessTime,
            ["mempo_stats"] = mempoStats,
            ["emotional_stats"] = emotionalStats
        };
    }

    public string ForceRetrieveContext(string userId = "", string query = "")
    {
        var parts = new List<string>();

        if (_mempo.IsValueCreated)
        {
            var (context, _) = _mempo.Value.BuildContext(query);
            if (!string.IsNullOrEmpty(context))
                parts.Add(context);
        }

        if (_emotional.IsValueCreated)
        {
            var emotionalContext = _emotional.Value.EmotionalContext();
            if (emotionalContext.Intensity > 0f)
            {
                parts.Add($"Emotional context: dominant={emotionalContext.DominantEmotion}, "
                          + $"intensity={emotionalContext.Intensity:F2}, valence={emotionalContext.Valence:F2}");
            }
        }

        return string.Join("\n", parts);
    }

    private static float ScorePersonaFact(PersonaFact fact, string query)
    {
        if (string.IsNullOrEmpty(query))
            return 0f;

        var factLower = fact.Fact.ToLowerInvariant();
        var queryLower = query.ToLowerInvariant();

        var queryWords = new HashSet<string>(queryLower.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var factWords = new HashSet<string>(factLower.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        if (queryWords.Count == 0)
            return 0f;

        var hits = queryWords.Count(factWords.Contains);
        return (float)hits / queryWords.Count;
    }
}
