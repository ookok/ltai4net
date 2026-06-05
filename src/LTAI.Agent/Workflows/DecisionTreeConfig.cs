// Copyright (c) LTAI. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LTAI.Agent.Workflows;

/// <summary>
/// P15 hot-editable configuration for <see cref="DecisionTreeRouter"/>. Loaded
/// from <c>.livingtree/workflows/decision-tree.json</c> on first call and on
/// file-change events emitted by <see cref="YAMLWorkflowWatcher"/>.
/// </summary>
/// <remarks>
/// <para><b>JSON schema:</b></para>
/// <code>
/// {
///   "type": "decision-tree",
///   "version": 1,
///   "topK": 3,
///   "confidenceMarginThreshold": 0.15,
///   "minTopScoreThreshold": 0.30,
///   "ambiguousFallback": "all",   // all | topK | none
///   "candidates": []              // empty = all specialists; else whitelist
/// }
/// </code>
/// <para>
/// The file is read by <see cref="YAMLWorkflowRegistry"/>; the router itself
/// queries <c>DecisionTreeConfig.Current</c> (volatile ref) on every call, so
/// reload is automatic — no router instance rebuild required.
/// </para>
/// <para><b>Why JSON not YAML:</b> LTAI.Agent has no YamlDotNet dependency.
/// MAF declarative workflows (<c>greeting.yaml</c>) are parsed by the
/// <c>Microsoft.Agents.ObjectModel</c> NuGet package which brings its own YAML
/// parser. LTAI's own configs use <see cref="System.Text.Json"/> for zero-dep
/// serialization, matching <c>SessionManager</c> / <c>SnippetStore</c>.
/// </para>
/// </remarks>
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

    [JsonPropertyName("candidates")]
    public List<string> Candidates { get; set; } = new();

    /// <summary>
    /// File path used to load this config. Populated by <see cref="LoadFromFile"/>
    /// for diagnostics; not serialized.
    /// </summary>
    [JsonIgnore]
    public string? SourcePath { get; set; }

    /// <summary>
    /// Fallback used when no JSON file is present. Hardcoded to mirror the
    /// original P7.7 <c>DecisionTreeRouterOptions</c> defaults so behavior is
    /// unchanged when the user has not authored <c>decision-tree.json</c>.
    /// </summary>
    public static DecisionTreeConfig Default => new();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Parse a JSON string into a <see cref="DecisionTreeConfig"/>. Throws on
    /// schema errors (the registry catches and surfaces to <c>ILogger</c>).
    /// </summary>
    public static DecisionTreeConfig Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("decision-tree.json is empty");
        var cfg = JsonSerializer.Deserialize<DecisionTreeConfig>(json, _jsonOptions)
                  ?? throw new InvalidOperationException("decision-tree.json deserialization returned null");
        // P14.9 review: validate key fields to catch silent misconfiguration
        if (cfg.TopK < 1 || cfg.TopK > 20)
            throw new InvalidOperationException($"topK={cfg.TopK} is out of range [1, 20]");
        if (cfg.ConfidenceMarginThreshold is < 0f or > 1f)
            throw new InvalidOperationException($"confidenceMarginThreshold={cfg.ConfidenceMarginThreshold} is out of range [0, 1]");
        if (cfg.MinTopScoreThreshold is < 0f or > 1f)
            throw new InvalidOperationException($"minTopScoreThreshold={cfg.MinTopScoreThreshold} is out of range [0, 1]");
        return cfg;
    }

    /// <summary>
    /// Load from <paramref name="path"/> or return <see cref="Default"/> when
    /// the file does not exist. Throws on parse errors.
    /// </summary>
    public static DecisionTreeConfig LoadFromFile(string path)
    {
        if (!File.Exists(path)) return new DecisionTreeConfig { SourcePath = path };
        var json = File.ReadAllText(path);
        var cfg = Parse(json);
        cfg.SourcePath = path;
        return cfg;
    }

    /// <summary>
    /// Resolve a fallback strategy enum from the JSON string. Invalid values
    /// fall back to <see cref="AmbiguousFallbackKind.All"/>.
    /// </summary>
    [JsonIgnore]
    public AmbiguousFallbackKind FallbackKind => AmbiguousFallback?.ToLowerInvariant() switch
    {
        "topk" => AmbiguousFallbackKind.TopK,
        "none" => AmbiguousFallbackKind.None,
        _ => AmbiguousFallbackKind.All,
    };
}

public enum AmbiguousFallbackKind
{
    /// <summary>Hand off to every registered specialist (slowest, highest recall).</summary>
    All,
    /// <summary>Hand off to the top-K candidates from the embedder (faster, mid recall).</summary>
    TopK,
    /// <summary>Return a "no confident match" response; do not invoke any specialist.</summary>
    None,
}
