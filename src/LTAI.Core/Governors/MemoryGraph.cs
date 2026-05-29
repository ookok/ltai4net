using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using LTAI.Core.Interfaces;
using LTAI.Core.System;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Governors;

public sealed record MemoryNode
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string Content { get; init; } = "";
    public string Summary { get; init; } = "";
    public float[]? Embedding { get; init; }
    public int LayerLevel { get; init; } // 0=detail, 1=summary, 2=concept, 3=domain
    public string Domain { get; init; } = "general";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;
    public int AccessCount { get; set; }
    public double Importance { get; set; } = 0.5;
    public HashSet<string> Tags { get; init; } = new();
    public Dictionary<string, string> Metadata { get; init; } = new();
}

public sealed record MemoryEdge
{
    public string SourceId { get; init; } = "";
    public string TargetId { get; init; } = "";
    public MemoryEdgeType Type { get; init; }
    public double Weight { get; init; } = 1.0;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

public enum MemoryEdgeType
{
    SimilarTo,     // System-1: semantic similarity edge
    ParentOf,      // System-2: hierarchical parent-child
    ChildOf,       // reverse: child → parent
    CoOccurred,    // temporal proximity
    ReferencedBy   // explicit reference
}

public sealed class HierarchyLayer
{
    public int Level { get; init; }
    public string Label { get; init; } = ""; // detail, summary, concept, domain
    public int NodeCount => _nodes.Count;
    public double CompressionRatio { get; init; } = 1.0;
    public ConcurrentDictionary<string, MemoryNode> Nodes => _nodes;
    private readonly ConcurrentDictionary<string, MemoryNode> _nodes = new();
}

/// <summary>
/// Hierarchical memory graph implementing a four-level cognitive memory model:
///   Level 0 (detail): raw observations, conversation fragments
///   Level 1 (summary): auto-summarized groupings of detail nodes
///   Level 2 (concept): auto-conceptualized groupings of summary nodes
///   Level 3 (domain): domain-level abstractions (currently manual)
///
/// Auto-summarization and auto-conceptualization trigger when ≥5 nodes exist
/// at the source level in the same domain. Uses a pluggable summarizer function.
/// Inverted term index enables fast text-based search.
/// Event bus integration for cross-component memory change notifications.
///
/// Callers: LTAI.Agent.Prefetch, LTAI.Knowledge.Core.KnowledgeGraph,
///          LTAI.Core.Life.DigitalTwin.
/// Thread-safe: all collections are ConcurrentDictionary; term index uses
/// per-key locks for thread-safe set mutation.
/// </summary>
public sealed class MemoryGraph
{
    private readonly ConcurrentDictionary<string, MemoryNode> _nodes = new();
    private readonly ConcurrentDictionary<string, MemoryEdge> _edges = new();
    private readonly List<HierarchyLayer> _hierarchy = new();
    private readonly ILogger<MemoryGraph> _logger;
    private readonly int _maxNodes;
    private readonly Func<string, string, string>? _summarizer;
    private readonly IMemoryEventBus? _eventBus;
    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

    // Inverted index: lowercase term → set of node IDs containing that term
    private readonly ConcurrentDictionary<string, HashSet<string>> _termIndex = new(StringComparer.OrdinalIgnoreCase);
    private static readonly char[] IndexSplitChars = [' ', '\t', '\n', '\r', ',', '.', '!', '?', ';', ':', '(', ')', '[', ']', '{', '}', '"', '\'', '<', '>', '/', '\\', '|', '@', '#', '$', '%', '^', '&', '*', '+', '=', '~', '`'];

    public int NodeCount => _nodes.Count;
    public int EdgeCount => _edges.Count;
    public IReadOnlyList<HierarchyLayer> Hierarchy => _hierarchy.AsReadOnly();

    public MemoryGraph(int maxNodes = 10000, Func<string, string, string>? summarizer = null,
        ILogger<MemoryGraph>? logger = null, IMemoryEventBus? eventBus = null)
    {
        _maxNodes = maxNodes;
        _summarizer = summarizer;
        _logger = logger ?? NullLogger<MemoryGraph>.Instance;
        _eventBus = eventBus;

        _hierarchy.Add(new HierarchyLayer { Level = 0, Label = "detail", CompressionRatio = 1.0 });
        _hierarchy.Add(new HierarchyLayer { Level = 1, Label = "summary", CompressionRatio = 0.3 });
        _hierarchy.Add(new HierarchyLayer { Level = 2, Label = "concept", CompressionRatio = 0.1 });
        _hierarchy.Add(new HierarchyLayer { Level = 3, Label = "domain", CompressionRatio = 0.03 });
    }

    /// <summary>Event raised when the graph changes (node added/removed/pruned).</summary>
    public event Action<string, Dictionary<string, object>>? OnChange;

    private void PublishEvent(string eventType, Dictionary<string, object?> data)
    {
        // Convert Dictionary<string, object?> to Dictionary<string, object> for the OnChange event
        var onChangeData = data.ToDictionary(kv => kv.Key, kv => kv.Value!);
        OnChange?.Invoke(eventType, onChangeData);

        // Also publish to cross-component event bus
        if (_eventBus != null)
        {
            var memEventType = eventType switch
            {
                "add_node" => MemoryEventType.NodeAdded,
                "prune" => MemoryEventType.NodePruned,
                _ => MemoryEventType.NodeAdded
            };
            _eventBus.Publish(new MemoryEvent
            {
                Type = memEventType,
                Source = "MemoryGraph",
                Detail = eventType,
                Metadata = data
            });
        }
    }

    /// <summary>Add a node's terms to the inverted index.</summary>
    private void IndexNode(MemoryNode node)
    {
        var textToIndex = $"{node.Content} {node.Summary} {string.Join(" ", node.Tags)} {node.Domain}"
            .ToLowerInvariant();
        var terms = textToIndex.Split(IndexSplitChars, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 1)
            .Distinct();

        foreach (var term in terms)
        {
            var ids = _termIndex.GetOrAdd(term, _ => new HashSet<string>());
            lock (ids) { ids.Add(node.Id); }
        }
    }

    /// <summary>Remove a node's terms from the inverted index.</summary>
    private void RemoveFromIndex(MemoryNode node)
    {
        var terms = node.Content.ToLowerInvariant().Split(IndexSplitChars, StringSplitOptions.RemoveEmptyEntries)
            .Concat(node.Summary.ToLowerInvariant().Split(IndexSplitChars, StringSplitOptions.RemoveEmptyEntries))
            .Where(t => t.Length > 1)
            .Distinct();

        foreach (var term in terms)
        {
            if (_termIndex.TryGetValue(term, out var ids))
            {
                lock (ids) { ids.Remove(node.Id); }
            }
        }
    }

    public MemoryNode AddNode(MemoryNode node)
    {
        if (_nodes.Count >= _maxNodes)
            PruneStaleNodes();

        _nodes[node.Id] = node;
        var layer = _hierarchy.FirstOrDefault(l => l.Level == node.LayerLevel);
        layer?.Nodes[node.Id] = node;

        IndexNode(node);

        _logger.LogDebug("MemoryGraph: added node {Id} at layer {Layer} ({Domain})",
            node.Id, node.LayerLevel, node.Domain);

        if (node.LayerLevel == 0 && _summarizer != null)
            TryAutoSummarize(node);

        if (node.LayerLevel == 1 && _summarizer != null)
            TryAutoConceptualize(node);

        PublishEvent("add_node", new Dictionary<string, object?>
        {
            ["node_id"] = node.Id,
            ["layer"] = node.LayerLevel,
            ["domain"] = node.Domain,
            ["importance"] = node.Importance
        });

        return node;
    }

    private void TryAutoSummarize(MemoryNode detailNode)
    {
        var sameDomainDetails = _nodes.Values
            .Where(n => n.LayerLevel == 0 && n.Domain == detailNode.Domain)
            .ToList();

        if (sameDomainDetails.Count < 5) return;

        var existingSummary = _nodes.Values.FirstOrDefault(n =>
            n.LayerLevel == 1 && n.Domain == detailNode.Domain &&
            n.Summary.Contains("auto-summary"));

        if (existingSummary != null)
        {
            var combinedContent = string.Join(" | ", sameDomainDetails
                .OrderByDescending(n => n.AccessCount)
                .Take(10)
                .Select(n => n.Content.Length < 200 ? n.Content : n.Summary));
            var updated = existingSummary! with { Summary = _summarizer!(combinedContent, detailNode.Domain), LastAccessedAt = DateTime.UtcNow };
            _nodes[existingSummary.Id] = updated;
            var layer = _hierarchy.FirstOrDefault(l => l.Level == 1);
            layer?.Nodes[existingSummary.Id] = updated;
            return;
        }

        var contents = sameDomainDetails
            .OrderByDescending(n => n.Importance)
            .Take(10)
            .Select(n => n.Content.Length < 200 ? n.Content : n.Summary);

        var newSummary = new MemoryNode
        {
            LayerLevel = 1,
            Domain = detailNode.Domain,
            Summary = $"auto-summary: {_summarizer!(string.Join(" | ", contents), detailNode.Domain)}",
            Content = "",
            Importance = sameDomainDetails.Average(n => n.Importance) * 0.8,
            Tags = new HashSet<string>(sameDomainDetails.SelectMany(n => n.Tags).Distinct().Take(10)),
            Metadata = new Dictionary<string, string> { ["source"] = "auto-summarize" }
        };

        AddNode(newSummary);
        foreach (var detail in sameDomainDetails.Take(5))
        {
            AddEdge(new MemoryEdge
            {
                SourceId = newSummary.Id,
                TargetId = detail.Id,
                Type = MemoryEdgeType.ParentOf,
                Weight = 1.0 / (detail.Importance + 1)
            });
        }

        _logger.LogInformation("MemoryGraph: auto-summarized {Count} detail nodes in domain '{Domain}' → summary node {Id}",
            sameDomainDetails.Count, detailNode.Domain, newSummary.Id);
    }

    private void TryAutoConceptualize(MemoryNode summaryNode)
    {
        var sameDomainSummaries = _nodes.Values
            .Where(n => n.LayerLevel == 1 && n.Domain == summaryNode.Domain)
            .ToList();

        if (sameDomainSummaries.Count < 5) return;

        var existingConcept = _nodes.Values.FirstOrDefault(n =>
            n.LayerLevel == 2 && n.Domain == summaryNode.Domain &&
            n.Summary.Contains("auto-concept"));

        if (existingConcept != null)
        {
            var combinedContent = string.Join("\n", sameDomainSummaries
                .OrderByDescending(n => n.Importance)
                .Take(8)
                .Select(n => n.Summary));
            var updated = existingConcept with { Summary = $"auto-concept: {_summarizer!(combinedContent, summaryNode.Domain)}", LastAccessedAt = DateTime.UtcNow };
            _nodes[existingConcept.Id] = updated;
            var layer = _hierarchy.FirstOrDefault(l => l.Level == 2);
            layer?.Nodes[existingConcept.Id] = updated;
            return;
        }

        var summaries = sameDomainSummaries
            .OrderByDescending(n => n.Importance)
            .Take(8)
            .Select(n => n.Summary);

        var tags = new HashSet<string>(sameDomainSummaries
            .SelectMany(n => n.Tags)
            .GroupBy(t => t)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => g.Key));

        var newConcept = new MemoryNode
        {
            LayerLevel = 2,
            Domain = summaryNode.Domain,
            Summary = $"auto-concept: {_summarizer!(string.Join("\n", summaries), summaryNode.Domain)}",
            Content = "",
            Importance = sameDomainSummaries.Average(n => n.Importance) * 0.6,
            Tags = tags,
            Metadata = new Dictionary<string, string> { ["source"] = "auto-conceptualize" }
        };

        AddNode(newConcept);
        foreach (var summary in sameDomainSummaries.Take(5))
        {
            AddEdge(new MemoryEdge
            {
                SourceId = newConcept.Id,
                TargetId = summary.Id,
                Type = MemoryEdgeType.ParentOf,
                Weight = 1.0 / (summary.Importance + 1)
            });
        }

        _logger.LogInformation("MemoryGraph: auto-conceptualized {Count} summary nodes in domain '{Domain}' → concept node {Id}",
            sameDomainSummaries.Count, summaryNode.Domain, newConcept.Id);
    }

    public void TriggerAutoConceptualize(string domain)
    {
        var summaries = _nodes.Values
            .Where(n => n.LayerLevel == 1 && n.Domain == domain)
            .ToList();

        if (summaries.Count == 0) return;

        var dummy = summaries[0] with { };
        if (_summarizer != null)
            TryAutoConceptualize(dummy);
    }

    public void TriggerAutoSummarize(string domain)
    {
        var details = _nodes.Values
            .Where(n => n.LayerLevel == 0 && n.Domain == domain)
            .ToList();

        if (details.Count == 0) return;

        var dummy = details[0] with { };
        if (_summarizer != null)
            TryAutoSummarize(dummy);
    }

    public MemoryEdge AddEdge(MemoryEdge edge)
    {
        var key = $"{edge.SourceId}->{edge.TargetId}::{edge.Type}";
        _edges[key] = edge;

        if (edge.Type == MemoryEdgeType.ParentOf)
        {
            var reverse = new MemoryEdge
            {
                SourceId = edge.TargetId,
                TargetId = edge.SourceId,
                Type = MemoryEdgeType.ChildOf,
                Weight = edge.Weight
            };
            _edges[$"{reverse.SourceId}->{reverse.TargetId}::{reverse.Type}"] = reverse;
        }

        return edge;
    }

    public MemoryNode? GetNode(string id)
    {
        if (_nodes.TryGetValue(id, out var node))
        {
            node.LastAccessedAt = DateTime.UtcNow;
            node.AccessCount++;
            // Importance: never drop below current, only grow.
            // Base: 0.5 (initial) scaled by access frequency + content richness bonus
            var accessImportance = 0.5 + Math.Min(node.AccessCount * 0.03, 0.4);
            var contentBonus = Math.Min(node.Content.Length / 5000.0, 0.1);
            var newImportance = Math.Min(1.0, accessImportance + contentBonus);
            node.Importance = Math.Max(node.Importance, newImportance);
            return node;
        }
        return null;
    }

    public IReadOnlyList<MemoryNode> GetChildren(string parentId)
    {
        var children = new List<MemoryNode>();
        foreach (var kv in _edges)
        {
            if (kv.Value.SourceId == parentId && kv.Value.Type == MemoryEdgeType.ParentOf)
            {
                var child = GetNode(kv.Value.TargetId);
                if (child != null) children.Add(child);
            }
        }
        return children;
    }

    public IReadOnlyList<MemoryNode> GetNeighbors(string nodeId, int maxDepth = 1)
    {
        var visited = new HashSet<string> { nodeId };
        var result = new List<MemoryNode>();

        var queue = new Queue<(string id, int depth)>();
        queue.Enqueue((nodeId, 0));

        while (queue.Count > 0)
        {
            var (currentId, depth) = queue.Dequeue();
            if (depth > maxDepth) continue;

            foreach (var kv in _edges)
            {
                var isSource = kv.Value.SourceId == currentId;
                var isTarget = kv.Value.TargetId == currentId;
                if (!isSource && !isTarget) continue;

                var neighborId = isSource ? kv.Value.TargetId : kv.Value.SourceId;
                if (!visited.Add(neighborId)) continue;

                var neighbor = GetNode(neighborId);
                if (neighbor != null)
                {
                    result.Add(neighbor);
                    queue.Enqueue((neighborId, depth + 1));
                }
            }
        }

        return result;
    }

    public IReadOnlyList<MemoryNode> QueryByDomain(string domain)
    {
        return _nodes.Values
            .Where(n => n.Domain == domain)
            .OrderByDescending(n => n.Importance)
            .ToList();
    }

    public IReadOnlyList<MemoryNode> QueryByLayer(int layerLevel)
    {
        return _nodes.Values
            .Where(n => n.LayerLevel == layerLevel)
            .OrderByDescending(n => n.Importance)
            .ToList();
    }

    public IReadOnlyList<MemoryNode> Search(string query, int topK = 10)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<MemoryNode>();

        var terms = query.Split(IndexSplitChars, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 1)
            .Select(t => t.ToLowerInvariant())
            .Distinct()
            .ToArray();

        if (terms.Length == 0)
            return new List<MemoryNode>();

        // Phase 1: Inverted index — find candidate node IDs
        var candidateIds = new HashSet<string>();
        foreach (var term in terms)
        {
            if (_termIndex.TryGetValue(term, out var ids))
            {
                lock (ids)
                {
                    foreach (var id in ids)
                        candidateIds.Add(id);
                }
            }
            // If a term isn't in the index, fall back to scanning all tags/domains
            // (these are short fields, cheap to scan)
            foreach (var node in _nodes.Values)
            {
                if (node.Tags.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                    node.Domain.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    candidateIds.Add(node.Id);
                }
            }
        }

        if (candidateIds.Count == 0)
            return new List<MemoryNode>();

        // Phase 2: Score candidates
        var scored = new List<(MemoryNode Node, double Score)>();
        var termSet = new HashSet<string>(terms);

        foreach (var id in candidateIds)
        {
            if (!_nodes.TryGetValue(id, out var node)) continue;

            double score = 0;
            // Count how many of the query terms appear (BM25-like term frequency)
            var contentLower = node.Content.ToLowerInvariant();
            var summaryLower = node.Summary.ToLowerInvariant();

            foreach (var term in termSet)
            {
                if (contentLower.Contains(term, StringComparison.Ordinal))
                    score += 1.0;
                if (summaryLower.Contains(term, StringComparison.Ordinal))
                    score += 2.0;
                if (node.Tags.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase)))
                    score += 3.0;
                if (node.Domain.Contains(term, StringComparison.OrdinalIgnoreCase))
                    score += 0.5;
            }

            if (score > 0)
            {
                score *= node.Importance;
                scored.Add((node, score));
            }
        }

        return scored
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x =>
            {
                x.Node.LastAccessedAt = DateTime.UtcNow;
                x.Node.AccessCount++;
                return x.Node;
            })
            .ToList();
    }

    /// <summary>
    /// Mem-π enhanced search: keyword retrieval first, then Mem-π generative guidance
    /// if retrieval confidence is low. Returns both retrieved nodes and any generated guidance.
    /// </summary>
    public async Task<MemPiSearchResult> SearchWithGuidanceAsync(
        string query,
        IMemPiGuidance? memPi,
        int topK = 10,
        double retrievalConfidenceThreshold = 0.3,
        CancellationToken ct = default)
    {
        // Phase 1: keyword retrieval (fast, always runs)
        var retrieved = Search(query, topK);
        var maxScore = retrieved.Count > 0
            ? retrieved.Max(n => n.Importance * n.AccessCount)
            : 0.0;
        var avgImportance = retrieved.Count > 0
            ? retrieved.Average(n => n.Importance)
            : 0.0;

        // Phase 2: Mem-π generative guidance if retrieval is weak
        string? generatedGuidance = null;
        var memPiUsed = false;

        if (memPi != null && memPi.IsAvailable &&
            (avgImportance < retrievalConfidenceThreshold || retrieved.Count < 3))
        {
            if (memPi.ShouldAttemptGuidance(query))
            {
                try
                {
                    var context = BuildContextFromRetrieved(retrieved);
                    var mpResult = await memPi.GenerateGuidanceAsync(context, query, ct).ConfigureAwait(false);
                    if (mpResult.Generated && !string.IsNullOrWhiteSpace(mpResult.Guidance))
                    {
                        generatedGuidance = mpResult.Guidance;
                        memPiUsed = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MemoryGraph: MemPi guidance failed, falling back to retrieval-only");
                }
            }
        }

        return new MemPiSearchResult
        {
            RetrievedNodes = retrieved,
            GeneratedGuidance = generatedGuidance,
            MemPiUsed = memPiUsed,
            RetrievalConfidence = avgImportance,
            MaxRetrievalScore = maxScore
        };
    }

    public IReadOnlyList<MemoryNode> TopDownTraverse(string? rootDomain = null, int maxResults = 20)
    {
        var results = new List<MemoryNode>();
        var seen = new HashSet<string>();

        var domains = rootDomain != null
            ? new[] { rootDomain }
            : _nodes.Values.Where(n => n.LayerLevel == 3).Select(n => n.Domain).Distinct();

        foreach (var domain in domains)
        {
            var domainNodes = QueryByDomain(domain)
                .Where(n => n.LayerLevel == 3)
                .Take(2);

            foreach (var domainNode in domainNodes)
            {
                if (seen.Add(domainNode.Id))
                    results.Add(domainNode);

                var children = GetChildren(domainNode.Id);
                foreach (var child in children.Take(3))
                {
                    if (results.Count >= maxResults) break;
                    if (seen.Add(child.Id))
                        results.Add(child);

                    var grandchildren = GetChildren(child.Id);
                    foreach (var gc in grandchildren.Take(2))
                    {
                        if (results.Count >= maxResults) break;
                        if (seen.Add(gc.Id))
                            results.Add(gc);
                    }
                }
            }

            if (results.Count >= maxResults) break;
        }

        return results;
    }

    public void BuildHierarchy()
    {
        foreach (var node in _nodes.Values.Where(n => n.LayerLevel > 0))
        {
            var lowerNodes = _nodes.Values
                .Where(n => n.LayerLevel == node.LayerLevel - 1 && n.Domain == node.Domain)
                .Take(5)
                .ToList();

            foreach (var lower in lowerNodes)
            {
                AddEdge(new MemoryEdge
                {
                    SourceId = node.Id,
                    TargetId = lower.Id,
                    Type = MemoryEdgeType.ParentOf,
                    Weight = 1.0 / (node.LayerLevel + 1)
                });
            }
        }

        _logger.LogInformation("MemoryGraph: hierarchy built — {NodeCount} nodes across {LayerCount} layers",
            _nodes.Count, _hierarchy.Count);
    }

    public void PruneStaleNodes()
    {
        var cutoff = DateTime.UtcNow.AddDays(-7);
        var staleIds = _nodes.Values
            .Where(n => n.LastAccessedAt < cutoff && n.Importance <= 0.3)
            .Select(n => n.Id)
            // Allow full cleanup — no arbitrary 10% cap
            .ToList();

        foreach (var id in staleIds)
        {
            if (_nodes.TryRemove(id, out var removedNode))
            {
                RemoveFromIndex(removedNode);
            }
            // Also remove from hierarchy layers
            foreach (var layer in _hierarchy)
                layer.Nodes.TryRemove(id, out _);
            // Remove all incident edges
            var edgeKeys = _edges.Where(kv => kv.Value.SourceId == id || kv.Value.TargetId == id)
                .Select(kv => kv.Key).ToList();
            foreach (var key in edgeKeys) _edges.TryRemove(key, out _);
        }

        if (staleIds.Count > 0)
            _logger.LogInformation("MemoryGraph: pruned {Count} stale nodes (remaining={Remaining})",
                staleIds.Count, _nodes.Count);
    }

    private static string BuildContextFromRetrieved(IReadOnlyList<MemoryNode> retrieved)
    {
        if (retrieved.Count == 0) return "(no recent memories)";
        return string.Join(" | ", retrieved.Take(5).Select(n =>
            n.Content.Length < 100 ? n.Content : n.Summary));
    }
}

/// <summary>Result of Mem-π enhanced memory search.</summary>
public sealed record MemPiSearchResult
{
    public IReadOnlyList<MemoryNode> RetrievedNodes { get; init; } = Array.Empty<MemoryNode>();
    public string? GeneratedGuidance { get; init; }
    public bool MemPiUsed { get; init; }
    public double RetrievalConfidence { get; init; }
    public double MaxRetrievalScore { get; init; }
}

/// <summary>
/// Background service that periodically prunes stale nodes from MemoryGraph.
/// Runs every hour and cleans nodes that haven't been accessed in 7+ days with Importance ≤ 0.3.
/// </summary>
public sealed class MemoryGraphCleanupService : IHostedService, IDisposable
{
    private readonly MemoryGraph _graph;
    private readonly ILogger<MemoryGraphCleanupService> _logger;
    private Timer? _timer;

    public MemoryGraphCleanupService(MemoryGraph graph, ILogger<MemoryGraphCleanupService> logger)
    {
        _graph = graph;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _timer = new Timer(DoCleanup, null, TimeSpan.FromMinutes(10), TimeSpan.FromHours(1));
        _logger.LogInformation("MemoryGraphCleanupService started (interval=1h, initial=10min)");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _logger.LogInformation("MemoryGraphCleanupService stopped");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }

    private void DoCleanup(object? state)
    {
        try
        {
            var before = _graph.NodeCount;
            _graph.PruneStaleNodes();
            var after = _graph.NodeCount;
            if (before > 0)
                _logger.LogInformation("MemoryGraph cleanup: {Before} → {After} nodes ({Removed} removed)",
                    before, after, before - after);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MemoryGraph cleanup cycle failed");
        }
    }
}
