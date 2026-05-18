using System.Text.Json.Serialization;

namespace LTAI.Core.Models;

public sealed record Handshake
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];

    [JsonPropertyName("from")]
    public string From { get; init; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; init; } = string.Empty;

    [JsonPropertyName("action")]
    public string Action { get; init; } = string.Empty;

    [JsonPropertyName("payload")]
    public Dictionary<string, object?>? Payload { get; init; }

    [JsonPropertyName("priority")]
    public HandshakePriority Priority { get; init; } = HandshakePriority.Normal;

    [JsonPropertyName("ttl_ms")]
    public int TtlMs { get; init; } = 30000;

    [JsonPropertyName("reply_to")]
    public string? ReplyTo { get; init; }
}
