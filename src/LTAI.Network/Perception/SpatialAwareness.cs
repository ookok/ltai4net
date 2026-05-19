using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace LTAI.Network.Perception;

public enum ZoneType
{
    Device,
    Room,
    Floor,
    Building,
    Region,
    Cloud,
    Unknown
}

public enum Proximity
{
    SameDevice,
    SameLan,
    SameGateway,
    RemoteLowLatency,
    Remote,
    Unknown
}

public sealed record SpatialNode
{
    [JsonPropertyName("node_id")]
    public string NodeId { get; init; } = string.Empty;

    [JsonPropertyName("zone_type")]
    public ZoneType ZoneType { get; init; }

    [JsonPropertyName("proximity")]
    public Proximity Proximity { get; init; }

    [JsonPropertyName("entities_present")]
    public List<string> EntitiesPresent { get; init; } = new();

    [JsonPropertyName("signal_strength")]
    public double SignalStrength { get; init; }

    [JsonPropertyName("latency_ms")]
    public int LatencyMs { get; init; }
}

public sealed record SpatialEdge
{
    [JsonPropertyName("source_id")]
    public string SourceId { get; init; } = string.Empty;

    [JsonPropertyName("target_id")]
    public string TargetId { get; init; } = string.Empty;

    [JsonPropertyName("proximity")]
    public Proximity Proximity { get; init; }

    [JsonPropertyName("latency_ms")]
    public int LatencyMs { get; init; }

    [JsonPropertyName("bandwidth_hint")]
    public double BandwidthHint { get; init; }

    [JsonPropertyName("encrypted")]
    public bool Encrypted { get; init; }
}

public sealed record RoomModel
{
    [JsonPropertyName("zone_id")]
    public string ZoneId { get; init; } = string.Empty;

    [JsonPropertyName("nodes")]
    public List<string> Nodes { get; init; } = new();

    [JsonPropertyName("entities")]
    public List<string> Entities { get; init; } = new();

    [JsonPropertyName("gateway_node")]
    public string GatewayNode { get; init; } = string.Empty;

    [JsonPropertyName("parent_zone")]
    public string? ParentZone { get; init; }

    [JsonPropertyName("density")]
    public double Density { get; init; }
}

public sealed record TriangulationResult
{
    [JsonPropertyName("entity_id")]
    public string EntityId { get; init; } = string.Empty;

    [JsonPropertyName("observing_nodes")]
    public List<string> ObservingNodes { get; init; } = new();

    [JsonPropertyName("confidence")]
    public double Confidence { get; init; }

    [JsonPropertyName("estimated_zone")]
    public string EstimatedZone { get; init; } = string.Empty;

    [JsonPropertyName("consensus_score")]
    public double ConsensusScore { get; init; }

    [JsonPropertyName("conflicting_nodes")]
    public List<string> ConflictingNodes { get; init; } = new();
}

public sealed class SpatialAwareness
{
    private static readonly Lazy<SpatialAwareness> _instance = new(() => new SpatialAwareness());

    public static SpatialAwareness Instance => _instance.Value;

    private readonly ILogger<SpatialAwareness>? _logger;
    private readonly ConcurrentDictionary<string, SpatialNode> _nodes = new();
    private readonly ConcurrentDictionary<string, SpatialEdge> _edges = new();
    private readonly ConcurrentDictionary<string, RoomModel> _rooms = new();
    private readonly ConcurrentDictionary<string, List<TriangulationResult>> _observations = new();

    public SpatialAwareness()
    {
    }

    public SpatialAwareness(ILogger<SpatialAwareness> logger)
    {
        _logger = logger;
    }

    public void RegisterNode(SpatialNode node)
    {
        _nodes[node.NodeId] = node;
        _logger?.LogDebug("Node registered: {NodeId} ({ZoneType})", node.NodeId, node.ZoneType);
    }

    public void RegisterEdge(string sourceId, string targetId, SpatialEdge edge)
    {
        var key = $"{sourceId}→{targetId}";
        _edges[key] = edge;
        _logger?.LogDebug("Edge registered: {Source} → {Target}", sourceId, targetId);
    }

    public void RegisterSelf(string nodeId, ZoneType zoneType, List<string> entities)
    {
        var node = new SpatialNode
        {
            NodeId = nodeId,
            ZoneType = zoneType,
            Proximity = Proximity.SameDevice,
            EntitiesPresent = entities,
            SignalStrength = 1.0,
            LatencyMs = 0
        };
        _nodes[nodeId] = node;
        _logger?.LogInformation("Self registered: {NodeId} ({ZoneType})", nodeId, zoneType);
    }

    public void RegisterPeer(string peerId, Proximity proximity, int latencyMs)
    {
        var node = new SpatialNode
        {
            NodeId = peerId,
            ZoneType = _proximityToZoneType(proximity),
            Proximity = proximity,
            EntitiesPresent = new List<string>(),
            SignalStrength = _bandwidthHint(latencyMs),
            LatencyMs = latencyMs
        };
        _nodes[peerId] = node;
    }

    public TriangulationResult? LocateEntity(string entityId)
    {
        _observations.TryGetValue(entityId, out var results);
        if (results == null || results.Count == 0)
            return null;
        return results.OrderByDescending(r => r.Confidence).First();
    }

    public RoomModel DefineRoom(string zoneId, List<string> nodes, List<string> entities, string gateway)
    {
        var room = new RoomModel
        {
            ZoneId = zoneId,
            Nodes = nodes,
            Entities = entities,
            GatewayNode = gateway,
            Density = nodes.Count > 0 ? entities.Count / (double)nodes.Count : 0.0
        };
        _rooms[zoneId] = room;
        return room;
    }

    public List<RoomModel> AutoDiscoverRooms()
    {
        var discovered = new List<RoomModel>();
        var nodeEntries = _nodes.Values.ToList();

        var groups = nodeEntries
            .GroupBy(n => n.Proximity)
            .Where(g => g.Key != Proximity.Remote && g.Key != Proximity.Unknown);

        foreach (var group in groups)
        {
            var nodeIds = group.Select(n => n.NodeId).ToList();
            var entities = group.SelectMany(n => n.EntitiesPresent).Distinct().ToList();
            var gateway = nodeIds.FirstOrDefault() ?? string.Empty;
            var zoneId = $"auto-{group.Key.ToString().ToLowerInvariant()}-{nodeIds.Count}";

            var room = DefineRoom(zoneId, nodeIds, entities, gateway);
            room = room with { ParentZone = "auto-discovered" };
            _rooms[zoneId] = room;
            discovered.Add(room);
        }

        _logger?.LogInformation("Auto-discovered {Count} rooms", discovered.Count);
        return discovered;
    }

    public TriangulationResult Triangulate(string entityId)
    {
        var observingNodes = new List<string>();
        var zoneVotes = new Dictionary<string, int>();
        var conflictingNodes = new List<string>();
        var totalVotes = 0;

        foreach (var (nodeId, node) in _nodes)
        {
            if (node.EntitiesPresent.Contains(entityId))
            {
                observingNodes.Add(nodeId);
                var zoneKey = node.ZoneType.ToString();
                zoneVotes.TryGetValue(zoneKey, out var count);
                zoneVotes[zoneKey] = count + 1;
                totalVotes++;
            }
        }

        if (zoneVotes.Count == 0)
        {
            return new TriangulationResult
            {
                EntityId = entityId,
                ObservingNodes = observingNodes,
                Confidence = 0.5,
                EstimatedZone = ZoneType.Unknown.ToString(),
                ConsensusScore = 0.0,
                ConflictingNodes = conflictingNodes
            };
        }

        var bestZone = zoneVotes.OrderByDescending(kv => kv.Value).First();
        var consensusScore = totalVotes > 0 ? bestZone.Value / (double)totalVotes : 0.0;

        foreach (var (nodeId, node) in _nodes)
        {
            if (node.EntitiesPresent.Contains(entityId) && node.ZoneType.ToString() != bestZone.Key)
                conflictingNodes.Add(nodeId);
        }

        var confidence = Math.Max(0.5, consensusScore * (1.0 - conflictingNodes.Count / Math.Max(1.0, (double)observingNodes.Count)));

        return new TriangulationResult
        {
            EntityId = entityId,
            ObservingNodes = observingNodes,
            Confidence = Math.Round(confidence, 4),
            EstimatedZone = bestZone.Key,
            ConsensusScore = Math.Round(consensusScore, 4),
            ConflictingNodes = conflictingNodes
        };
    }

    public List<SpatialNode> NearestNodes(string originId, int maxHops = 3)
    {
        return _getReachableNodes(originId, Math.Max(1, maxHops));
    }

    public List<string> EntitiesInZone(string zoneId)
    {
        var entities = new HashSet<string>();

        if (_rooms.TryGetValue(zoneId, out var room))
        {
            foreach (var e in room.Entities)
                entities.Add(e);
            foreach (var nodeId in room.Nodes)
            {
                if (_nodes.TryGetValue(nodeId, out var node))
                {
                    foreach (var e in node.EntitiesPresent)
                        entities.Add(e);
                }
            }
        }

        var matchingNodes = _nodes.Values.Where(n => n.ZoneType.ToString().Equals(zoneId, StringComparison.OrdinalIgnoreCase));
        foreach (var node in matchingNodes)
        {
            foreach (var e in node.EntitiesPresent)
                entities.Add(e);
        }

        return entities.ToList();
    }

    public string GetSpatialReport()
    {
        return $"Nodes: {_nodes.Count} | Edges: {_edges.Count} | Rooms: {_rooms.Count} | "
               + $"Entity observations: {_observations.Count} | Total entities tracked: {_observations.Sum(o => o.Value.Count)}";
    }

    private List<SpatialNode> _getReachableNodes(string originId, int maxHops)
    {
        var visited = new HashSet<string>();
        var result = new List<SpatialNode>();
        var queue = new Queue<(string nodeId, int depth)>();

        if (!_nodes.ContainsKey(originId))
            return result;

        queue.Enqueue((originId, 0));
        visited.Add(originId);

        while (queue.Count > 0)
        {
            var (currentId, depth) = queue.Dequeue();
            if (_nodes.TryGetValue(currentId, out var node))
                result.Add(node);

            if (depth >= maxHops)
                continue;

            foreach (var edge in _edges.Values)
            {
                if (edge.SourceId == currentId && !visited.Contains(edge.TargetId))
                {
                    visited.Add(edge.TargetId);
                    queue.Enqueue((edge.TargetId, depth + 1));
                }
                if (edge.TargetId == currentId && !visited.Contains(edge.SourceId))
                {
                    visited.Add(edge.SourceId);
                    queue.Enqueue((edge.SourceId, depth + 1));
                }
            }
        }

        return result;
    }

    private static double _bandwidthHint(int latencyMs)
    {
        if (latencyMs <= 5) return 1.0;
        if (latencyMs <= 10) return 0.9;
        if (latencyMs >= 500) return 0.1;
        return 0.9 - ((latencyMs - 10) / 490.0) * 0.8;
    }

    private static ZoneType _proximityToZoneType(Proximity proximity)
    {
        return proximity switch
        {
            Proximity.SameDevice => ZoneType.Device,
            Proximity.SameLan => ZoneType.Room,
            Proximity.SameGateway => ZoneType.Building,
            Proximity.RemoteLowLatency => ZoneType.Region,
            Proximity.Remote => ZoneType.Cloud,
            _ => ZoneType.Unknown
        };
    }
}
