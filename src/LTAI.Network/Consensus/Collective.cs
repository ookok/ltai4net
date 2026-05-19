using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Network.Consensus;

public sealed record SharedKnowledge
{
    [JsonPropertyName("source_node")]
    public string SourceNode { get; init; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("conversation_dna")]
    public List<string> ConversationDna { get; init; } = new();

    [JsonPropertyName("classifier_weights")]
    public Dictionary<string, double> ClassifierWeights { get; init; } = new();

    [JsonPropertyName("pipeline_templates")]
    public List<string> PipelineTemplates { get; init; } = new();

    [JsonPropertyName("life_events")]
    public List<string> LifeEvents { get; init; } = new();

    [JsonPropertyName("generation")]
    public int Generation { get; init; } = 0;
}

public sealed class CollectiveConsciousness
{
    private static readonly Lazy<CollectiveConsciousness> _instance = new(() => new CollectiveConsciousness());
    public static CollectiveConsciousness Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, SharedKnowledge> _knowledge = new();
    private readonly int _syncInterval = 300;
    private readonly ILogger<CollectiveConsciousness> _logger;
    private DateTime _lastSync = DateTime.MinValue;

    private CollectiveConsciousness()
    {
        _logger = NullLogger<CollectiveConsciousness>.Instance;
    }

    public async Task ShareWithPeersAsync(
        SharedKnowledge knowledge,
        List<string> peers,
        CancellationToken cancellationToken = default)
    {
        foreach (var peer in peers)
        {
            _logger.LogDebug("Sharing knowledge with peer {Peer}: Gen={Generation}, DNA={DnaCount}",
                peer, knowledge.Generation, knowledge.ConversationDna.Count);
        }

        _logger.LogInformation("Knowledge shared with {PeerCount} peers", peers.Count);
        _lastSync = DateTime.UtcNow;

        await Task.CompletedTask;
    }

    public SharedKnowledge? ReceiveFromPeer(SharedKnowledge knowledge)
    {
        var key = $"{knowledge.SourceNode}_{knowledge.Timestamp:yyyyMMddHHmmssfff}";

        if (_knowledge.TryAdd(key, knowledge))
        {
            _logger.LogInformation(
                "Received knowledge from {Source}: Gen={Generation}, DNA={DnaCount}, Weights={WeightCount}",
                knowledge.SourceNode, knowledge.Generation,
                knowledge.ConversationDna.Count, knowledge.ClassifierWeights.Count);

            return knowledge;
        }

        _logger.LogDebug("Duplicate knowledge from {Source}, skipped", knowledge.SourceNode);
        return null;
    }

    public SharedKnowledge CollectKnowledge(
        List<string> conversationDna,
        Dictionary<string, double> classifierWeights)
    {
        var knowledge = new SharedKnowledge
        {
            SourceNode = Environment.MachineName,
            ConversationDna = conversationDna,
            ClassifierWeights = classifierWeights,
            PipelineTemplates = new List<string>(),
            LifeEvents = new List<string>(),
            Generation = _knowledge.Count + 1
        };

        _knowledge.TryAdd(
            $"{knowledge.SourceNode}_{knowledge.Timestamp:yyyyMMddHHmmssfff}",
            knowledge);

        _logger.LogInformation("Collected knowledge: Gen={Generation}", knowledge.Generation);
        return knowledge;
    }

    public Dictionary<string, object> GetStatus()
    {
        return new Dictionary<string, object>
        {
            ["knowledge_count"] = _knowledge.Count,
            ["peers_shared"] = _knowledge.Values.Select(k => k.SourceNode).Distinct().Count(),
            ["last_sync"] = _lastSync == DateTime.MinValue ? "never" : _lastSync.ToString("O"),
            ["sync_interval_seconds"] = _syncInterval
        };
    }
}
