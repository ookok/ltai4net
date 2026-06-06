// Copyright (c) LTAI. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LTAI.Agent.Workflows;

public sealed class DecisionTreeConfig
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "decision-tree";

    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("topK")]
    public int TopK { get; set; } = 3;

    [JsonPropertyName("confidenceMarginThreshold")]
    public float ConfidenceMarginThreshold { get; set; } = 0.15f;

    [JsonPropertyName("minTopScoreThreshold")]
    public float MinTopScoreThreshold { get; set; } = 0.30f;

    [JsonPropertyName("ambiguousFallback")]
    public string AmbiguousFallback { get; set; } = "all";

    [JsonPropertyName("minAcceptableScore")]
    public float MinAcceptableScore { get; set; } = 0.05f;

    [JsonPropertyName("candidates")]
    public List<string> Candidates { get; set; } = new();

    [JsonPropertyName("mcpTriggers")]
    public List<McpTriggerConfig> McpTriggers { get; set; } = new();

    [JsonIgnore]
    public string? SourcePath { get; set; }

    public static DecisionTreeConfig Default => new();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static DecisionTreeConfig Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("decision-tree.json is empty");
        var cfg = JsonSerializer.Deserialize<DecisionTreeConfig>(json, _jsonOptions)
                  ?? throw new InvalidOperationException("decision-tree.json deserialization returned null");
        if (cfg.TopK < 1 || cfg.TopK > 20)
            throw new InvalidOperationException($"topK={cfg.TopK} is out of range [1, 20]");
        if (cfg.ConfidenceMarginThreshold is < 0f or > 1f)
            throw new InvalidOperationException($"confidenceMarginThreshold={cfg.ConfidenceMarginThreshold} is out of range [0, 1]");
        if (cfg.MinTopScoreThreshold is < 0f or > 1f)
            throw new InvalidOperationException($"minTopScoreThreshold={cfg.MinTopScoreThreshold} is out of range [0, 1]");
        if (cfg.MinAcceptableScore is < 0f or > 1f)
            throw new InvalidOperationException($"minAcceptableScore={cfg.MinAcceptableScore} is out of range [0, 1]");
        return cfg;
    }

    public static DecisionTreeConfig LoadFromFile(string path)
    {
        if (!File.Exists(path)) return new DecisionTreeConfig { SourcePath = path };
        var json = File.ReadAllText(path);
        var cfg = Parse(json);
        cfg.SourcePath = path;
        return cfg;
    }

    [JsonIgnore]
    public AmbiguousFallbackKind FallbackKind => AmbiguousFallback?.ToLowerInvariant() switch
    {
        "topk" => AmbiguousFallbackKind.TopK,
        "none" => AmbiguousFallbackKind.None,
        _ => AmbiguousFallbackKind.All,
    };
}

public sealed class McpTriggerConfig
{
    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = "";

    [JsonPropertyName("workflow")]
    public string Workflow { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

public enum AmbiguousFallbackKind
{
    All,
    TopK,
    None,
}
