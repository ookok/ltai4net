using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace LTAI.Network.Perception;

public enum SignalSource
{
    Interaction,
    Resource,
    Network,
    Temporal,
    Behavioral
}

public enum EntityType
{
    HumanUser,
    PassivePresence,
    PeerNode,
    ExternalAgent,
    Ambient,
    Unknown
}

public enum ActivityLevel
{
    Idle,
    Low,
    Active,
    Intense,
    Burst
}

public enum DriftSeverity
{
    None,
    Mild,
    Moderate,
    Severe,
    Isolated
}

public sealed record SignalEvent
{
    [JsonPropertyName("source")]
    public SignalSource Source { get; init; }

    [JsonPropertyName("signal_type")]
    public string SignalType { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public double Value { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("metadata")]
    public string? Metadata { get; init; }
}

public sealed record PresenceEntity
{
    [JsonPropertyName("entity_id")]
    public string EntityId { get; init; } = string.Empty;

    [JsonPropertyName("entity_type")]
    public EntityType EntityType { get; init; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; init; }

    [JsonPropertyName("activity_level")]
    public ActivityLevel ActivityLevel { get; init; }

    [JsonPropertyName("signal_signature")]
    public List<double> SignalSignature { get; init; } = new();

    [JsonPropertyName("location_hint")]
    public string? LocationHint { get; init; }

    [JsonPropertyName("peer_node_id")]
    public string? PeerNodeId { get; init; }

    [JsonPropertyName("last_seen")]
    public DateTime LastSeen { get; init; } = DateTime.UtcNow;

    [JsonIgnore]
    public bool IsStale => (DateTime.UtcNow - LastSeen).TotalSeconds > 300;
}

public sealed record GhostDetection
{
    [JsonPropertyName("detected")]
    public bool Detected { get; init; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; init; }

    [JsonPropertyName("evidence")]
    public List<string> Evidence { get; init; } = new();

    [JsonPropertyName("estimated_distance")]
    public double EstimatedDistance { get; init; }
}

public sealed record PresenceShare
{
    [JsonPropertyName("node_id")]
    public string NodeId { get; init; } = string.Empty;

    [JsonPropertyName("entities")]
    public List<PresenceEntity> Entities { get; init; } = new();

    [JsonPropertyName("activity_summary")]
    public string ActivitySummary { get; init; } = string.Empty;

    [JsonPropertyName("ghost_detection")]
    public GhostDetection GhostDetection { get; init; } = new();

    [JsonPropertyName("spatial_snapshot")]
    public string? SpatialSnapshot { get; init; }

    [JsonPropertyName("ttl_hops")]
    public int TtlHops { get; init; } = 3;
}

public sealed record FusedObservation
{
    [JsonPropertyName("entity_id")]
    public string EntityId { get; init; } = string.Empty;

    [JsonPropertyName("node_sightings")]
    public int NodeSightings { get; init; }

    [JsonPropertyName("fused_confidence")]
    public double FusedConfidence { get; init; }

    [JsonPropertyName("consensus")]
    public double Consensus { get; init; }

    [JsonPropertyName("activity")]
    public ActivityLevel Activity { get; init; }

    [JsonPropertyName("location_consensus")]
    public string? LocationConsensus { get; init; }

    [JsonPropertyName("conflicts")]
    public int Conflicts { get; init; }
}

public sealed record DriftAlert
{
    [JsonPropertyName("node_id")]
    public string NodeId { get; init; } = string.Empty;

    [JsonPropertyName("severity")]
    public DriftSeverity Severity { get; init; }

    [JsonPropertyName("entity_id")]
    public string EntityId { get; init; } = string.Empty;

    [JsonPropertyName("node_confidence")]
    public double NodeConfidence { get; init; }

    [JsonPropertyName("consensus_confidence")]
    public double ConsensusConfidence { get; init; }

    [JsonPropertyName("divergence")]
    public double Divergence { get; init; }
}

public sealed class P2PPresence
{
    private static readonly Lazy<P2PPresence> _instance = new(() => new P2PPresence());

    public static P2PPresence Instance => _instance.Value;

    private readonly ILogger<P2PPresence>? _logger;
    private readonly ConcurrentDictionary<string, PresenceEntity> _entities = new();
    private readonly ConcurrentDictionary<SignalSource, List<SignalEvent>> _signalWindows = new();
    private readonly ConcurrentDictionary<string, FusedObservation> _fused = new();
    private readonly ConcurrentDictionary<string, List<double>> _divergenceHistory = new();

    private const int MaxSignalWindow = 200;
    private const int MaxDivergenceSamples = 20;
    private const int StaleTimeoutSeconds = 900;
    private const double GhostThreshold = 0.35;
    private const double InteractionDensityThreshold = 0.15;
    private const double PeerHeartbeatThreshold = 0.2;
    private const double BaselineDeviationThreshold = 0.25;

    private static readonly SignalSource[] AllSources = Enum.GetValues<SignalSource>();

    public P2PPresence()
    {
        foreach (var source in AllSources)
            _signalWindows[source] = new List<SignalEvent>();
    }

    public P2PPresence(ILogger<P2PPresence> logger) : this()
    {
        _logger = logger;
    }

    public void FeedSignal(SignalSource source, string signalType, double value, string? metadata = null)
    {
        var evt = new SignalEvent
        {
            Source = source,
            SignalType = signalType,
            Value = Math.Clamp(value, 0.0, 1.0),
            Timestamp = DateTime.UtcNow,
            Metadata = metadata
        };

        var list = _signalWindows.GetOrAdd(source, _ => new List<SignalEvent>());
        lock (list)
        {
            list.Add(evt);
            while (list.Count > MaxSignalWindow)
                list.RemoveAt(0);
        }
    }

    public List<PresenceEntity> Detect()
    {
        var detected = new List<PresenceEntity>();

        var human = _detectHumanUser();
        if (human != null) detected.Add(human);

        var peers = _detectPeers();
        detected.AddRange(peers);

        var passive = _detectPassivePresence();
        if (passive != null) detected.Add(passive);

        foreach (var entity in detected)
            _entities[entity.EntityId] = entity;

        return detected;
    }

    public GhostDetection DetectGhost()
    {
        return _detectGhost();
    }

    public string GetReport()
    {
        var entities = _entities.Values.ToList();
        var ghost = _detectGhost();

        var lines = new List<string>
        {
            $"Entities tracked: {entities.Count}",
            $"Ghost detection: {(ghost.Detected ? $"Yes (cf={ghost.Confidence:F2})" : "No")}",
            ""
        };

        foreach (var entity in entities.OrderByDescending(e => e.Confidence))
        {
            lines.Add($"  {entity.EntityId}: {entity.EntityType} | "
                      + $"cf={entity.Confidence:F2} | {entity.ActivityLevel} | "
                      + $"stale={entity.IsStale} | last={entity.LastSeen:HH:mm:ss}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    public PresenceShare BuildShare()
    {
        var entities = _entities.Values.Where(e => !e.IsStale).ToList();
        var ghost = _detectGhost();
        var activityLevels = entities.Select(e => e.ActivityLevel).ToList();

        var summary = activityLevels.Count > 0
            ? $"Active entities: {entities.Count} | Dominant: {activityLevels.GroupBy(a => a).OrderByDescending(g => g.Count()).First().Key}"
            : "No active entities";

        return new PresenceShare
        {
            NodeId = "local",
            Entities = entities,
            ActivitySummary = summary,
            GhostDetection = ghost,
            TtlHops = 3
        };
    }

    public void ReceiveShare(PresenceShare share)
    {
        if (share.TtlHops <= 0)
            return;

        foreach (var entity in share.Entities)
        {
            _fuseEntity(entity);
        }

        _logger?.LogDebug("Received presence share from {NodeId} with {Count} entities",
            share.NodeId, share.Entities.Count);
    }

    public List<FusedObservation> GetFusedEntities()
    {
        return _fused.Values.ToList();
    }

    public DriftAlert? CheckDrift(string nodeId, double nodeConfidence)
    {
        var currentFused = _fused.Values.ToList();
        if (currentFused.Count == 0)
            return null;

        var avgConsensus = currentFused.Average(f => f.Consensus);
        var divergence = Math.Abs(nodeConfidence - avgConsensus);

        var history = _divergenceHistory.GetOrAdd(nodeId, _ => new List<double>());
        lock (history)
        {
            history.Add(divergence);
            while (history.Count > MaxDivergenceSamples)
                history.RemoveAt(0);
        }

        if (history.Count < 3)
            return null;

        var mean = history.Average();
        var std = history.Count > 1
            ? Math.Sqrt(history.Sum(d => (d - mean) * (d - mean)) / (history.Count - 1))
            : 0.0;

        var zScore = std > 0 ? Math.Abs((divergence - mean) / std) : 0.0;

        DriftSeverity severity = zScore switch
        {
            < 1.0 => DriftSeverity.None,
            < 2.0 => DriftSeverity.Mild,
            < 3.0 => DriftSeverity.Moderate,
            < 4.0 => DriftSeverity.Severe,
            _ => DriftSeverity.Isolated
        };

        if (severity == DriftSeverity.None)
            return null;

        return new DriftAlert
        {
            NodeId = nodeId,
            Severity = severity,
            NodeConfidence = nodeConfidence,
            ConsensusConfidence = avgConsensus,
            Divergence = divergence
        };
    }

    public void PruneStaleEntities()
    {
        var staleIds = _entities.Values
            .Where(e => (DateTime.UtcNow - e.LastSeen).TotalSeconds > StaleTimeoutSeconds)
            .Select(e => e.EntityId)
            .ToList();

        foreach (var id in staleIds)
        {
            _entities.TryRemove(id, out _);
            _fused.TryRemove(id, out _);
        }

        if (staleIds.Count > 0)
            _logger?.LogInformation("Pruned {Count} stale entities", staleIds.Count);
    }

    public (int EntityCount, Dictionary<string, int> ByType, int FusedCount, int SignalsReceived) Stats()
    {
        var byType = _entities.Values
            .GroupBy(e => e.EntityType.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        var signalsReceived = _signalWindows.Values.Sum(list =>
        {
            lock (list) { return list.Count; }
        });

        return (_entities.Count, byType, _fused.Count, signalsReceived);
    }

    private PresenceEntity? _detectHumanUser()
    {
        var interactionSignals = GetWindow(SignalSource.Interaction);
        if (interactionSignals.Count == 0)
            return null;

        var recent = interactionSignals
            .Where(s => (DateTime.UtcNow - s.Timestamp).TotalSeconds < 120)
            .ToList();

        if (recent.Count == 0)
            return null;

        var density = recent.Count / 120.0;
        if (density < InteractionDensityThreshold)
            return null;

        var avgValue = recent.Average(s => s.Value);
        var signature = recent.Select(s => s.Value).Take(16).ToList();

        var activityLevel = density switch
        {
            >= 1.0 => ActivityLevel.Burst,
            >= 0.5 => ActivityLevel.Intense,
            >= 0.25 => ActivityLevel.Active,
            >= InteractionDensityThreshold => ActivityLevel.Low,
            _ => ActivityLevel.Idle
        };

        var entityId = $"human-{Guid.NewGuid():N}"[..12];

        return new PresenceEntity
        {
            EntityId = entityId,
            EntityType = EntityType.HumanUser,
            Confidence = Math.Clamp(avgValue * (density / 0.5), 0.0, 1.0),
            ActivityLevel = activityLevel,
            SignalSignature = signature,
            LastSeen = DateTime.UtcNow
        };
    }

    private List<PresenceEntity> _detectPeers()
    {
        var peers = new List<PresenceEntity>();
        var networkSignals = GetWindow(SignalSource.Network);

        if (networkSignals.Count == 0)
            return peers;

        var peerGroups = networkSignals
            .Where(s => (DateTime.UtcNow - s.Timestamp).TotalSeconds < 300)
            .GroupBy(s => s.SignalType)
            .Where(g => g.Count() >= 2);

        foreach (var group in peerGroups)
        {
            var heartbeat = group.Count() / 300.0;
            if (heartbeat < PeerHeartbeatThreshold)
                continue;

            var avgValue = group.Average(s => s.Value);
            var entityId = $"peer-{group.Key}";
            var signature = group.Select(s => s.Value).Take(8).ToList();

            var entity = new PresenceEntity
            {
                EntityId = entityId,
                EntityType = EntityType.PeerNode,
                Confidence = Math.Clamp(avgValue * (heartbeat / 0.5), 0.0, 1.0),
                ActivityLevel = heartbeat >= 1.0 ? ActivityLevel.Active : ActivityLevel.Low,
                SignalSignature = signature,
                PeerNodeId = group.Key,
                LastSeen = group.Max(s => s.Timestamp)
            };
            peers.Add(entity);
        }

        return peers;
    }

    private PresenceEntity? _detectPassivePresence()
    {
        var resourceSignals = GetWindow(SignalSource.Resource);
        if (resourceSignals.Count < 3)
            return null;

        var recent = resourceSignals
            .Where(s => (DateTime.UtcNow - s.Timestamp).TotalSeconds < 300)
            .ToList();

        if (recent.Count == 0)
            return null;

        var mean = recent.Average(s => s.Value);
        var std = recent.Count > 1
            ? Math.Sqrt(recent.Sum(s => (s.Value - mean) * (s.Value - mean)) / (recent.Count - 1))
            : 0.0;

        if (std < BaselineDeviationThreshold)
            return null;

        var confidence = Math.Clamp(std * 2.0, 0.0, 1.0);
        var signature = recent.Select(s => s.Value).Take(8).ToList();

        return new PresenceEntity
        {
            EntityId = $"passive-{Guid.NewGuid():N}"[..12],
            EntityType = EntityType.PassivePresence,
            Confidence = confidence,
            ActivityLevel = std >= 0.5 ? ActivityLevel.Active : ActivityLevel.Low,
            SignalSignature = signature,
            LastSeen = DateTime.UtcNow
        };
    }

    private GhostDetection _detectGhost()
    {
        var evidence = new List<string>();
        var allEntities = _entities.Values.ToList();

        if (allEntities.Count == 0)
            return new GhostDetection { Detected = false, Confidence = 0.0, Evidence = evidence, EstimatedDistance = 0.0 };

        var staleEntities = allEntities.Where(e => e.IsStale).ToList();
        var activeEntities = allEntities.Where(e => !e.IsStale).ToList();

        if (staleEntities.Count == 0)
            return new GhostDetection { Detected = false, Confidence = 0.0, Evidence = evidence, EstimatedDistance = 0.0 };

        var ghostRatio = staleEntities.Count / (double)Math.Max(1, allEntities.Count);
        var avgStaleConfidence = staleEntities.Average(e => e.Confidence);

        if (ghostRatio > GhostThreshold && avgStaleConfidence > GhostThreshold)
        {
            evidence.Add($"Stale ratio: {ghostRatio:F2}");
            evidence.Add($"Previous confidence: {avgStaleConfidence:F2}");

            if (activeEntities.Count == 0)
                evidence.Add("No current active entities");

            var confidence = Math.Clamp(ghostRatio * avgStaleConfidence * 2.0, 0.0, 1.0);

            return new GhostDetection
            {
                Detected = true,
                Confidence = confidence,
                Evidence = evidence,
                EstimatedDistance = 1.0 - confidence
            };
        }

        return new GhostDetection { Detected = false, Confidence = 0.0, Evidence = evidence, EstimatedDistance = 0.0 };
    }

    private void _fuseEntity(PresenceEntity entity)
    {
        var existingObs = _fused.GetOrAdd(entity.EntityId, _ => new FusedObservation
        {
            EntityId = entity.EntityId,
            NodeSightings = 0,
            FusedConfidence = entity.Confidence,
            Consensus = 0.0,
            Activity = entity.ActivityLevel,
            LocationConsensus = entity.LocationHint,
            Conflicts = 0
        });

        var newNodeSightings = existingObs.NodeSightings + 1;
        var consensusBonus = Math.Min(0.3, newNodeSightings * 0.05);
        var fusedConfidence = existingObs.FusedConfidence * (1.0 + consensusBonus);
        fusedConfidence = Math.Clamp(fusedConfidence, 0.0, 1.0);

        var consensus = newNodeSightings > 1
            ? 1.0 - (1.0 / newNodeSightings)
            : 0.0;

        var locationConflict = entity.LocationHint != null
            && existingObs.LocationConsensus != null
            && entity.LocationHint != existingObs.LocationConsensus;

        var conflicts = existingObs.Conflicts + (locationConflict ? 1 : 0);

        _fused[entity.EntityId] = existingObs with
        {
            NodeSightings = newNodeSightings,
            FusedConfidence = Math.Round(fusedConfidence, 4),
            Consensus = Math.Round(consensus, 4),
            Activity = entity.ActivityLevel > existingObs.Activity ? entity.ActivityLevel : existingObs.Activity,
            LocationConsensus = locationConflict ? existingObs.LocationConsensus : entity.LocationHint ?? existingObs.LocationConsensus,
            Conflicts = conflicts
        };
    }

    private List<SignalEvent> GetWindow(SignalSource source)
    {
        var list = _signalWindows.GetOrAdd(source, _ => new List<SignalEvent>());
        lock (list)
        {
            return list.ToList();
        }
    }
}
