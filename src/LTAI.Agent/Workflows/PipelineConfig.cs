// Copyright (c) LTAI. All rights reserved.

using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace LTAI.Agent.Workflows;

/// <summary>
/// P16.1: hot-editable config block for Sequential / Concurrent pipeline presets.
/// Defined in <c>ltai-workflows/sequential.json</c> and
/// <c>concurrent.json</c>, hot-reloaded by the P15 watcher.
/// </summary>
public sealed record PipelineConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>"sequential" or "concurrent".</summary>
    public required string Type { get; init; }

    /// <summary>Schema version (bump to invalidate cache).</summary>
    public int Version { get; init; }

    /// <summary>Ordered agent names. For concurrent, order is cosmetic.</summary>
    public required IReadOnlyList<string> Agents { get; init; }

    /// <summary>Optional default task description used when caller provides no explicit task.</summary>
    public string? DefaultTask { get; init; }

    /// <summary>
    /// Parse from a JSON string (supports <c>//</c> comments).
    /// </summary>
    public static PipelineConfig Parse(string json)
    {
        var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString() ?? "";
        var version = root.TryGetProperty("version", out var v) ? v.GetInt32() : 0;
        var agents = new List<string>();
        foreach (var a in root.GetProperty("agents").EnumerateArray())
        {
            var name = a.GetString();
            if (!string.IsNullOrWhiteSpace(name))
                agents.Add(name);
        }

        return new PipelineConfig
        {
            Type = type,
            Version = version,
            Agents = agents,
        };
    }

    /// <summary>
    /// Try to load a <see cref="PipelineConfig"/> from file path.
    /// Returns <c>null</c> if the file doesn't exist or fails to parse.
    /// </summary>
    public static PipelineConfig? LoadFromFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return Parse(json);
        }
        catch
        {
            return null;
        }
    }
}
