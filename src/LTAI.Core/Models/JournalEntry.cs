using System.Text.Json.Serialization;

namespace LTAI.Core.Models;

public sealed record JournalEntry
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];

    [JsonPropertyName("action")]
    public string Action { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public JournalStatus Status { get; set; } = JournalStatus.Pending;

    [JsonPropertyName("started_at")]
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("completed_at")]
    public DateTime? CompletedAt { get; set; }

    [JsonPropertyName("duration_ms")]
    public double? DurationMs { get; set; }

    [JsonPropertyName("result")]
    public string? Result { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, object?>? Metadata { get; init; }
}
