// Copyright (c) LTAI. All rights reserved.

using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace LTAI.Agent.Workflows;

/// <summary>
/// P16.1: hot-editable config block for Sequential / Concurrent pipeline presets.
/// Supports both flat &lt;c&gt;agents&lt;/c&gt; array and nested &lt;c&gt;steps&lt;/c&gt; with typed
/// pipeline steps (handoff / sequential / concurrent).
/// Defined in &lt;c&gt;ltai-workflows/sequential.json&lt;/c&gt; and
/// &lt;c&gt;concurrent.json&lt;/c&gt;, hot-reloaded by the P15 watcher.
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

    /// <summary>Flat agent names (backward-compatible).</summary>
    public IReadOnlyList<string> Agents { get; init; } = [];

    /// <summary>
    /// Typed pipeline steps (P1.3). When non-empty, Agents is ignored.
    /// </summary>
    public IReadOnlyList<PipelineStep> Steps { get; init; } = [];

    /// <summary>Optional default task description.</summary>
    public string? DefaultTask { get; init; }

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
        if (root.TryGetProperty("agents", out var agentsEl))
        {
            foreach (var a in agentsEl.EnumerateArray())
            {
                var name = a.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                    agents.Add(name);
            }
        }

        var steps = new List<PipelineStep>();
        if (root.TryGetProperty("steps", out var stepsEl))
        {
            steps.AddRange(ParseSteps(stepsEl));
        }

        return new PipelineConfig
        {
            Type = type,
            Version = version,
            Agents = agents,
            Steps = steps,
        };
    }

    private static IEnumerable<PipelineStep> ParseSteps(JsonElement el)
    {
        foreach (var s in el.EnumerateArray())
        {
            var stepType = s.GetProperty("type").GetString() ?? "handoff";
            var name = s.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var stepAgents = new List<string>();
            if (s.TryGetProperty("agents", out var a))
            {
                foreach (var agent in a.EnumerateArray())
                {
                    var an = agent.GetString();
                    if (!string.IsNullOrWhiteSpace(an))
                        stepAgents.Add(an);
                }
            }
            var subSteps = new List<PipelineStep>();
            if (s.TryGetProperty("steps", out var sub))
            {
                subSteps.AddRange(ParseSteps(sub));
            }
            yield return new PipelineStep
            {
                Type = stepType,
                Name = name,
                Agents = stepAgents,
                Steps = subSteps,
            };
        }
    }

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

/// <summary>
/// A single step in a pipeline config. Supports nesting for composite workflows.
/// </summary>
public sealed record PipelineStep
{
    public required string Type { get; init; }
    public string Name { get; init; } = "";
    public IReadOnlyList<string> Agents { get; init; } = [];
    public IReadOnlyList<PipelineStep> Steps { get; init; } = [];
}
