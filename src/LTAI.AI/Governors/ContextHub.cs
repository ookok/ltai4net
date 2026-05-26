using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public enum ContextDomain { Memory, Knowledge, Skill, Session, Evolution, Harness, Conversation, Map, Synaptic }

public enum ContextKind { Entity, Episode, Skill, Lesson, Intervention, MapEntry, Synapse, ConversationTurn }

public sealed record ContextItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public ContextDomain Domain { get; init; }
    public ContextKind Kind { get; init; }
    public string Summary { get; init; } = "";
    public string? Detail { get; init; }
    public float Relevance { get; init; }
    public float Confidence { get; init; } = 0.5f;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public int UseCount { get; set; }
    public Dictionary<string, string> Links { get; init; } = new();
}

public sealed record ContextLink
{
    public ContextItem Source { get; init; } = null!;
    public ContextItem Target { get; init; } = null!;
    public string RelationType { get; init; } = "";
    public float Strength { get; init; } = 0.5f;
}

public interface IContextHub
{
    List<ContextItem> Query(string query, ContextDomain[]? domains = null, int topK = 10);
    List<ContextLink> TraceRelation(string entityId, int maxDepth = 2);
    void RegisterStore(ContextDomain domain, Func<string, int, List<ContextItem>> queryFn);
    void Evict(double contextBudget);
    int TotalItems { get; }
    Dictionary<ContextDomain, int> ItemsByDomain { get; }
}

public sealed class ContextHub : IContextHub
{
    private readonly ConcurrentDictionary<ContextDomain, Func<string, int, List<ContextItem>>> _stores = new();
    private readonly ILogger<ContextHub> _logger;

    public int TotalItems => _stores.Values.Sum(fn => fn("__stats__", int.MaxValue).Count);
    public Dictionary<ContextDomain, int> ItemsByDomain =>
        _stores.ToDictionary(k => k.Key, v => v.Value("__stats__", int.MaxValue).Count);

    public ContextHub(ILogger<ContextHub>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ContextHub>.Instance;
    }

    public void RegisterStore(ContextDomain domain, Func<string, int, List<ContextItem>> queryFn)
    {
        _stores[domain] = queryFn;
        _logger.LogDebug("ContextHub: registered store {Domain}", domain);
    }

    public List<ContextItem> Query(string query, ContextDomain[]? domains = null, int topK = 10)
    {
        var targetDomains = domains ?? _stores.Keys.ToArray();
        var allResults = new List<ContextItem>();

        foreach (var domain in targetDomains)
        {
            if (!_stores.TryGetValue(domain, out var queryFn)) continue;
            try
            {
                var results = queryFn(query, topK * 2);
                allResults.AddRange(results);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "ContextHub: query failed for {Domain}", domain); }
        }

        return allResults
            .OrderByDescending(i => i.Relevance)
            .ThenByDescending(i => i.UseCount)
            .ThenByDescending(i => i.Confidence)
            .Take(topK)
            .ToList();
    }

    public List<ContextLink> TraceRelation(string entityId, int maxDepth = 2)
    {
        var links = new List<ContextLink>();
        var visited = new HashSet<string>();

        var seeds = Query(entityId, topK: 20);
        var queue = new Queue<(ContextItem item, int depth)>();
        foreach (var seed in seeds) queue.Enqueue((seed, 0));

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (!visited.Add(current.Id) || depth >= maxDepth) continue;

            foreach (var (linkType, targetId) in current.Links)
            {
                var targets = Query(targetId, topK: 3);
                foreach (var target in targets)
                {
                    links.Add(new ContextLink
                    {
                        Source = current, Target = target,
                        RelationType = linkType,
                        Strength = current.Relevance * target.Relevance
                    });
                    if (depth + 1 < maxDepth)
                        queue.Enqueue((target, depth + 1));
                }
            }
        }

        return links;
    }

    public void Evict(double contextBudget)
    {
        var allItems = Query("__evict__", topK: int.MaxValue);
        var priorityThreshold = contextBudget / Math.Max(allItems.Count, 1);

        var toEvict = allItems
            .Where(i => i.Relevance * i.Confidence < priorityThreshold && i.UseCount < 3)
            .ToList();

        foreach (var item in toEvict)
            _logger.LogDebug("ContextHub: would evict {Kind}:{Id} (priority={Pri:F3})", item.Kind, item.Id, item.Relevance * item.Confidence);
    }
}
