using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Cell.Lifecycle;

public sealed class Mitosis
{
    private static readonly Lazy<Mitosis> _instance = new(() =>
        new Mitosis(NullLoggerFactory.Instance.CreateLogger<Mitosis>()));

    public static Mitosis Instance => _instance.Value;

    private readonly List<MitosisResult> _history = new();
    private readonly object _lock = new();
    private readonly Random _rng = new();
    private readonly ILogger<Mitosis> _logger;

    private const int MaxHistory = 200;

    public Mitosis() : this(NullLogger<Mitosis>.Instance) { }

    public Mitosis(ILogger<Mitosis> logger)
    {
        _logger = logger ?? NullLogger<Mitosis>.Instance;
    }

    public Task<MitosisResult> ForkModelAsync(string parentId, Dictionary<string, float> traits)
    {
        var childId = $"mito_{Guid.NewGuid().ToString("N")[..8]}";
        var mutatedTraits = new Dictionary<string, float>();

        foreach (var (key, value) in traits)
        {
            var noise = (float)((_rng.NextDouble() * 2 - 1) * 0.05);
            mutatedTraits[key] = Math.Clamp(value + noise, 0f, 1f);
        }

        var result = new MitosisResult
        {
            ParentId = parentId,
            ChildId = childId,
            ForkedAt = DateTime.UtcNow,
            GeneCount = traits.Count,
            Traits = mutatedTraits,
            Success = true
        };

        lock (_lock)
        {
            _history.Add(result);
            while (_history.Count > MaxHistory)
                _history.RemoveAt(0);
        }

        _logger.LogInformation("Mitosis: {ParentId} -> {ChildId} ({GeneCount} genes)", parentId, childId, traits.Count);
        return Task.FromResult(result);
    }

    public IReadOnlyList<MitosisResult> GetLineage(string parentId)
    {
        lock (_lock)
        {
            return _history.Where(r => r.ParentId == parentId).ToList();
        }
    }

    public IReadOnlyList<MitosisResult> ListClones()
    {
        lock (_lock)
        {
            return _history.ToList();
        }
    }

    public Dictionary<string, object> GetStats()
    {
        lock (_lock)
        {
            var count = _history.Count;
            var successCount = _history.Count(r => r.Success);
            var avgGeneCount = count > 0 ? _history.Average(r => r.GeneCount) : 0.0;

            return new Dictionary<string, object>
            {
                ["count"] = count,
                ["success_rate"] = count > 0 ? (double)successCount / count : 0.0,
                ["avg_gene_count"] = avgGeneCount
            };
        }
    }
}
