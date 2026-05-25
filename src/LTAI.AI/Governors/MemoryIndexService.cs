using System.Collections.Concurrent;
using LTAI.Knowledge.Core;
using LTAI.Knowledge.Memory;

namespace LTAI.AI.Governors;

/// <summary>
/// Unified memory index that maps event IDs across all 3 episodic stores:
/// TemporalMemoryFabric, StructMemory, and SynapticMemory.
/// Enables cross-store lookups and unified retrieval.
/// </summary>
public sealed class MemoryIndexService
{
    private readonly TemporalMemoryFabric _temporalMemory;
    private readonly StructMemory _structMemory;
    private readonly SynapticMemory _synapticMemory;
    private readonly ConcurrentDictionary<string, CrossReference> _index = new();

    public int TotalEntries => _index.Count;

    public MemoryIndexService(
        TemporalMemoryFabric temporalMemory,
        StructMemory structMemory,
        SynapticMemory synapticMemory)
    {
        _temporalMemory = temporalMemory;
        _structMemory = structMemory;
        _synapticMemory = synapticMemory;
    }

    /// <summary>
    /// Register an event across stores. Called whenever an event is created.
    /// </summary>
    public void Register(string unifiedId, string temporalId, string structId, string synapticId)
    {
        _index[unifiedId] = new CrossReference
        {
            UnifiedId = unifiedId,
            TemporalId = temporalId,
            StructId = structId,
            SynapticId = synapticId,
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Look up an event in all stores by any of its IDs.
    /// </summary>
    public CrossReference? Lookup(string anyId)
    {
        if (_index.TryGetValue(anyId, out var ref1)) return ref1;

        var match = _index.Values.FirstOrDefault(r =>
            r.TemporalId == anyId || r.StructId == anyId || r.SynapticId == anyId);
        return match;
    }

    /// <summary>
    /// Get all events older than specified age for pruning.
    /// </summary>
    public List<CrossReference> GetAgedEntries(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        return _index.Values.Where(r => r.CreatedAt < cutoff).ToList();
    }

    /// <summary>
    /// Remove entries from the index (does NOT remove from backing stores).
    /// </summary>
    public int Remove(IEnumerable<string> unifiedIds)
    {
        var count = 0;
        foreach (var id in unifiedIds)
            if (_index.TryRemove(id, out _)) count++;
        return count;
    }

    public Dictionary<string, object> GetStats() => new()
    {
        ["total_indexed"] = _index.Count,
        ["stores"] = new[] { "temporal", "struct", "synaptic" }
    };
}

public sealed class CrossReference
{
    public string UnifiedId { get; init; } = "";
    public string TemporalId { get; init; } = "";
    public string StructId { get; init; } = "";
    public string SynapticId { get; init; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Unified consolidation artifact replacing both StructMemory.SynthesisBlock 
/// and DreamCycle.AbstractLesson with a shared format.
/// </summary>
public sealed class MemoryArtifact
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string Type { get; init; } = "synthesis"; // synthesis | lesson | abstract | correction
    public string Content { get; init; } = "";
    public List<string> SourceEventIds { get; init; } = new();
    public double Confidence { get; init; } = 1.0;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public string? Domain { get; init; }
    public List<string> Tags { get; init; } = new();
    public Dictionary<string, double> Embedding { get; init; } = new();

    public static MemoryArtifact FromSynthesisBlock(string content, List<string> eventIds) => new()
    {
        Type = "synthesis", Content = content, SourceEventIds = eventIds
    };

    public static MemoryArtifact FromAbstractLesson(string lesson, List<string> eventIds, double confidence) => new()
    {
        Type = "lesson", Content = lesson, SourceEventIds = eventIds, Confidence = confidence
    };

    public static MemoryArtifact FromCorrection(string correction, List<string> eventIds) => new()
    {
        Type = "correction", Content = correction, SourceEventIds = eventIds
    };
}

/// <summary>
/// Adaptive retention policy: MemoryQualityMonitor measurements → RetentionPolicy thresholds.
/// When quality degrades, tighten retention; when quality is good, relax.
/// </summary>
public sealed class AdaptiveRetentionController
{
    private readonly MemoryQualityMonitor _qualityMonitor;
    private readonly double _defaultHalfLifeDays = 7.0;

    public AdaptiveRetentionController(MemoryQualityMonitor qualityMonitor)
    {
        _qualityMonitor = qualityMonitor;
    }

    /// <summary>
    /// Adjust retention thresholds based on the latest quality measurement.
    /// Better quality → longer retention (less forgetting).
    /// Returns recommended half-life in days.
    /// </summary>
    public (double HalfLifeDays, string Action) TuneFromQuality()
    {
        var history = _qualityMonitor.GetHistory(1);
        var latest = history.FirstOrDefault();
        if (latest == null) return (_defaultHalfLifeDays, "no_data");

        var advantageScore = Math.Max(latest.EpisodicAdvantage, latest.AbstractAdvantage);

        return advantageScore switch
        {
            > 0.3f => (14.0, "extend"),
            > 0.1f => (_defaultHalfLifeDays, "maintain"),
            > 0.05f => (3.0, "shorten"),
            _ => (1.0, "aggressive_prune")
        };
    }
}
