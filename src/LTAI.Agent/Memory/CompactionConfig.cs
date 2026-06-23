// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  CompactionConfig — YAML/JSON hot-reloadable compaction strategy
//
//  Loaded from .livingtree/workflows/compact-config.json via
//  YAMLWorkflowRegistry. CompactionStep reads this config on
//  each ProcessAsync call and applies threshold/tier settings.
// ═══════════════════════════════════════════════════════════════

using System.Text.Json;
using System.Text.Json.Serialization;

namespace LTAI.Agent.Memory;

public sealed class CompactionConfig
{
    public const string DefaultFileName = "compact-config.json";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "compaction";

    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    /// <summary>Progressive threshold levels.</summary>
    [JsonPropertyName("thresholds")]
    public ThresholdConfig Thresholds { get; init; } = new();

    /// <summary>Keep-last-N settings per threshold level.</summary>
    [JsonPropertyName("keep")]
    public KeepConfig Keep { get; init; } = new();

    /// <summary>Role-based compression settings.</summary>
    [JsonPropertyName("roles")]
    public RoleConfig Roles { get; init; } = new();

    /// <summary>Fidelity scoring enabled.</summary>
    [JsonPropertyName("fidelity")]
    public FidelityConfig Fidelity { get; init; } = new();

    /// <summary>Refs GC settings.</summary>
    [JsonPropertyName("gc")]
    public GcConfig Gc { get; init; } = new();

    /// <summary>Cross-session naming settings.</summary>
    [JsonPropertyName("crossSession")]
    public CrossSessionConfig CrossSession { get; init; } = new();

    public static CompactionConfig Default => new();

    public static CompactionConfig? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<CompactionConfig>(json); }
        catch { return null; }
    }

    public string ToJson() =>
        JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

public sealed class ThresholdConfig
{
    [JsonPropertyName("light")]
    public double Light { get; init; } = 0.60;

    [JsonPropertyName("moderate")]
    public double Moderate { get; init; } = 0.75;

    [JsonPropertyName("heavy")]
    public double Heavy { get; init; } = 0.85;

    [JsonPropertyName("critical")]
    public double Critical { get; init; } = 0.95;
}

public sealed class KeepConfig
{
    [JsonPropertyName("default")]
    public int Default { get; init; } = 10;

    [JsonPropertyName("light")]
    public int Light { get; init; } = 15;

    [JsonPropertyName("moderate")]
    public int Moderate { get; init; } = 8;

    [JsonPropertyName("heavy")]
    public int Heavy { get; init; } = 5;

    [JsonPropertyName("critical")]
    public int Critical { get; init; } = 1;
}

public sealed class RoleConfig
{
    [JsonPropertyName("keepUserLastN")]
    public int KeepUserLastN { get; init; } = 5;

    [JsonPropertyName("toolSummaryMaxChars")]
    public int ToolSummaryMaxChars { get; init; } = 500;

    [JsonPropertyName("assistantHeadChars")]
    public int AssistantHeadChars { get; init; } = 2000;
}

public sealed class FidelityConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("minReportable")]
    public double MinReportable { get; init; } = 0.3;
}

public sealed class GcConfig
{
    [JsonPropertyName("ttlHours")]
    public int TtlHours { get; init; } = 24;

    [JsonPropertyName("maxFiles")]
    public int MaxFiles { get; init; } = 10000;

    [JsonPropertyName("cleanupIntervalMinutes")]
    public int CleanupIntervalMinutes { get; init; } = 60;
}

public sealed class CrossSessionConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("maxRefsPerFile")]
    public int MaxRefsPerFile { get; init; } = 50;
}
