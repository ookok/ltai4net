using System.Text.Json;

namespace LTAI.Agent.Vector;

// ═══════════════════════════════════════════════
//  JSON serializer options
// ═══════════════════════════════════════════════

internal static partial class KgStoreInternals
{
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };
}

// ═══════════════════════════════════════════════
//  Data Transfer Objects
// ═══════════════════════════════════════════════

public sealed class NodeRow
{
    public long Id { get; set; }
    public string? ExtId { get; set; }
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Namespace { get; set; }
    public string? Signature { get; set; }
    public string? Source { get; set; }
    public string? Props { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";

    public Dictionary<string, object?>? GetProps()
    {
        if (string.IsNullOrEmpty(Props)) return null;
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(Props, KgStoreInternals.JsonOpts);
    }

    public override string ToString() => $"[{Kind}] {Name} ({Namespace})";
}

public sealed class EdgeRow
{
    public long Id { get; set; }
    public long Src { get; set; }
    public long Dst { get; set; }
    public string Relation { get; set; } = "";
    public double Weight { get; set; }
    public string? Props { get; set; }

    public override string ToString() => $"{Src} --[{Relation}]--> {Dst}";
}

public sealed class VersionRow
{
    public long Id { get; set; }
    public long NodeId { get; set; }
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string Snapshot { get; set; } = "";
    public string? Reason { get; set; }
    public string CreatedAt { get; set; } = "";

    public override string ToString() => $"v{Id}: {Reason ?? "edit"} @ {CreatedAt}";
}

public sealed class DocRow
{
    public long Id { get; set; }
    public long NodeId { get; set; }
    public string Text { get; set; } = "";
    public string? Lang { get; set; }
    public string? Source { get; set; }

    public override string ToString()
    {
        var snippet = Text.Length > 60 ? Text[..60] + "..." : Text;
        return $"Doc({Id}) for Node({NodeId}): {snippet}";
    }
}

public sealed class QualityScoreRow
{
    public long NodeId { get; set; }
    public double QualityScore { get; set; }
    public double FreshnessScore { get; set; }
    public double RelevanceScore { get; set; }
    public double ConfidenceScore { get; set; }
    public string ScoredAt { get; set; } = "";

    public override string ToString() =>
        $"Q={QualityScore:F2} (F={FreshnessScore:F2}, R={RelevanceScore:F2}, C={ConfidenceScore:F2})";
}
