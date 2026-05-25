using System.Text.Json.Serialization;

namespace LTAI.Infra.Network.Models;

public sealed class PeerInfo
{
    [JsonPropertyName("peer_id")]
    public string PeerId { get; init; } = string.Empty;

    [JsonPropertyName("address")]
    public string Address { get; init; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; init; }

    [JsonPropertyName("is_active")]
    public bool IsActive { get; init; }

    [JsonPropertyName("last_seen")]
    public DateTime LastSeen { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; init; } = new();
}

public sealed class NetworkMessage
{
    [JsonPropertyName("message_id")]
    public string MessageId { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("from_peer")]
    public string FromPeer { get; init; } = string.Empty;

    [JsonPropertyName("to_peer")]
    public string? ToPeer { get; init; }

    [JsonPropertyName("action")]
    public string Action { get; init; } = string.Empty;

    [JsonPropertyName("payload")]
    public string? Payload { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("priority")]
    public string Priority { get; init; } = "normal";

    [JsonPropertyName("ttl_ms")]
    public int TtlMs { get; init; } = 30000;

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    [JsonPropertyName("public_key")]
    public string? PublicKey { get; init; }
}

public sealed class DiscoveryRequest
{
    [JsonPropertyName("peer_id")]
    public string PeerId { get; init; } = string.Empty;

    [JsonPropertyName("address")]
    public string Address { get; init; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; init; }
}

public sealed class DiscoveryResponse
{
    [JsonPropertyName("known_peers")]
    public List<PeerInfo> KnownPeers { get; init; } = new();

    [JsonPropertyName("server_time")]
    public DateTime ServerTime { get; init; } = DateTime.UtcNow;
}
