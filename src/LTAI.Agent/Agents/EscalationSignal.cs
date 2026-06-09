using System.Text.Json.Serialization;

namespace LTAI.Agent;

public sealed record EscalationSignal
{
    public const string AdditionalPropertyKey = "x-ltai-needs-pro";

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = "";

    [JsonPropertyName("confidence")]
    public double Confidence { get; init; }

    [JsonPropertyName("source")]
    public string Source { get; init; } = "";

    [JsonPropertyName("span_indices")]
    public int[]? SpanIndices { get; init; }

    public string ToJson() =>
        System.Text.Json.JsonSerializer.Serialize(this);

    public static EscalationSignal? FromJson(string json)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<EscalationSignal>(json); }
        catch { return null; }
    }

    public static EscalationSignal? FromString(string raw)
    {
        var match = System.Text.RegularExpressions.Regex.Match(raw,
            @"<<<NEEDS_PRO:\s*(.+?)>>>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        return new EscalationSignal
        {
            Reason = match.Groups[1].Value.Trim(),
            Confidence = 1.0,
            Source = "llm-signal"
        };
    }

    public static string ToAdditionalProperties(string reason, double confidence = 1.0, string source = "quality-check")
    {
        return new EscalationSignal
        {
            Reason = reason,
            Confidence = confidence,
            Source = source
        }.ToJson();
    }
}
