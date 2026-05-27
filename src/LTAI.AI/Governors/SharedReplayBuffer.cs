using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.AI.Governors;

public sealed record PromptExperience
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Query { get; init; } = "";
    public string PromptVariant { get; init; } = "";
    public string Response { get; init; } = "";
    public double Reward { get; init; }
    public double Correctness { get; init; }
    public double Relevance { get; init; }
    public double Efficiency { get; init; }
    public string PolicyId { get; init; } = "";
    public string Domain { get; init; } = "general";
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public double Priority { get; init; } = 1.0;
    public Dictionary<string, object> Metadata { get; init; } = new();
}

public sealed class SharedReplayBuffer
{
    private readonly ConcurrentQueue<PromptExperience> _buffer = new();
    private readonly ConcurrentDictionary<string, List<PromptExperience>> _policyHistory = new();
    private readonly int _maxCapacity;
    private readonly ILogger<SharedReplayBuffer> _logger;
    private int _count;

    public SharedReplayBuffer(int maxCapacity = 10000, ILogger<SharedReplayBuffer>? logger = null)
    {
        _maxCapacity = maxCapacity;
        _logger = logger ?? NullLogger<SharedReplayBuffer>.Instance;
    }

    public int Count => _count;

    public void Push(PromptExperience experience)
    {
        if (Interlocked.Increment(ref _count) > _maxCapacity)
        {
            _buffer.TryDequeue(out _);
            Interlocked.Decrement(ref _count);
        }

        _buffer.Enqueue(experience);
        _policyHistory.AddOrUpdate(experience.PolicyId,
            _ => new List<PromptExperience> { experience },
            (_, list) =>
            {
                if (list.Count >= _maxCapacity / 4)
                    list.RemoveAt(0);
                list.Add(experience);
                return list;
            });

        _logger.LogDebug("Replay buffer: {Count}/{Max} experiences", _count, _maxCapacity);
    }

    public IReadOnlyList<PromptExperience> Sample(int batchSize, string? policyId = null, string? domain = null)
    {
        var candidates = policyId != null
            ? _policyHistory.GetValueOrDefault(policyId, new List<PromptExperience>()).AsEnumerable()
            : _buffer.AsEnumerable();

        if (domain != null)
            candidates = candidates.Where(e => e.Domain == domain);

        var weighted = candidates
            .Select(e => (Experience: e, Weight: e.Priority * e.Reward + 0.1))
            .ToList();

        if (weighted.Count == 0)
            return Array.Empty<PromptExperience>();

        var totalWeight = weighted.Sum(w => w.Weight);
        var random = new Random();
        var selected = new List<PromptExperience>();

        for (var i = 0; i < Math.Min(batchSize, weighted.Count); i++)
        {
            var r = random.NextDouble() * totalWeight;
            double cumulative = 0;
            foreach (var (exp, weight) in weighted)
            {
                cumulative += weight;
                if (cumulative >= r)
                {
                    selected.Add(exp);
                    break;
                }
            }
        }

        return selected;
    }

    public IReadOnlyList<PromptExperience> SampleRecent(TimeSpan window)
    {
        var cutoff = DateTime.UtcNow - window;
        return _buffer
            .Where(e => e.Timestamp >= cutoff)
            .OrderByDescending(e => e.Reward)
            .ToList();
    }

    public double GetAverageReward(string policyId)
    {
        var history = _policyHistory.GetValueOrDefault(policyId);
        if (history == null || history.Count == 0) return 0;
        return history.Average(e => e.Reward);
    }

    public IReadOnlyDictionary<string, double> GetPolicyStats()
    {
        var stats = new Dictionary<string, double>();
        foreach (var (policyId, history) in _policyHistory)
        {
            if (history.Count > 0)
                stats[policyId] = history.Average(e => e.Reward);
        }
        return stats;
    }

    public void Clear(string? policyId = null)
    {
        if (policyId == null)
        {
            while (_buffer.TryDequeue(out _)) { }
            _policyHistory.Clear();
            Interlocked.Exchange(ref _count, 0);
        }
        else
        {
            _policyHistory.TryRemove(policyId, out _);
        }
    }
}
