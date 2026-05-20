using System.Text.Json;
using System.Text.Json.Serialization;
using LTAI.Core.Configuration;
using LTAI.Core.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.MAF.Evolution;

public sealed class HarnessComponent
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("version")] public string Version { get; init; } = "1.0";
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; init; }
}

public sealed class HarnessManifest
{
    [JsonPropertyName("manifest_version")] public int Version { get; init; } = 1;
    [JsonPropertyName("timestamp")] public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    [JsonPropertyName("components")] public List<HarnessComponent> Components { get; init; } = new();
    [JsonPropertyName("tool_count")] public int ToolCount { get; init; }
    [JsonPropertyName("middleware_chain")] public List<string> MiddlewareChain { get; init; } = new();
    [JsonPropertyName("degradation_chain")] public Dictionary<string, string> DegradationChain { get; init; } = new();
}

public sealed class HarnessSnapshot
{
    private static readonly string ManifestDir = Path.Combine(".livingtree", "harness", "snapshots");
    private static readonly string ManifestPath = Path.Combine(".livingtree", "harness", "manifest.json");

    private readonly AIToolRegistry _toolRegistry;
    private readonly IOptions<LTAIOptions> _options;
    private readonly ILogger<HarnessSnapshot>? _logger;

    public HarnessSnapshot(AIToolRegistry toolRegistry, IOptions<LTAIOptions> options, ILogger<HarnessSnapshot>? logger = null)
    {
        _toolRegistry = toolRegistry;
        _options = options;
        _logger = logger;
        Directory.CreateDirectory(ManifestDir);
    }

    public HarnessManifest Capture()
    {
        var tools = _toolRegistry.GetTools().ToList();
        var options = _options.Value;

        var degradationChain = options.ModelPricing?.DegradationChain ?? new();
        var middlewareChain = new List<string> { "input_guard", "dna_safety", "prompt_shield", "tool_governance", "output_review" };

        var manifest = new HarnessManifest
        {
            ToolCount = tools.Count,
            MiddlewareChain = middlewareChain,
            DegradationChain = degradationChain,
            Components = new List<HarnessComponent>
            {
                new() { Name = "tools", Type = "tool_collection", Version = $"v{tools.Count}", Hash = ComputeHash(string.Join(",", tools.Select(t => t.GetType().Name).Order())) },
                new() { Name = "system_prompts", Type = "prompt_config", Version = "1.0", Hash = ComputeHash(options.AI.GetHashCode().ToString()) },
                new() { Name = "model_degradation", Type = "routing_config", Version = "1.0", Hash = ComputeHash(string.Join(";", degradationChain.Select(kv => $"{kv.Key}->{kv.Value}"))) },
                new() { Name = "middleware", Type = "middleware_chain", Version = "1.0", Hash = ComputeHash(string.Join(",", middlewareChain)) },
                new() { Name = "governors", Type = "governor_pipeline", Version = "10-gov", Description = "Input/Context/Routing/Capability/Storage/Output/Communication/Task/Self/Evolution" }
            }
        };

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ManifestPath, json);

        var snapPath = Path.Combine(ManifestDir, $"snap_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
        File.WriteAllText(snapPath, json);

        _logger?.LogInformation("Harness manifest captured: {Tools} tools, {Components} components", manifest.ToolCount, manifest.Components.Count);
        return manifest;
    }

    public HarnessManifest? LoadLatest()
    {
        if (!File.Exists(ManifestPath)) return null;
        return JsonSerializer.Deserialize<HarnessManifest>(File.ReadAllText(ManifestPath));
    }

    public List<string> ListSnapshots()
    {
        if (!Directory.Exists(ManifestDir)) return new();
        return Directory.GetFiles(ManifestDir, "snap_*.json")
            .OrderByDescending(f => f)
            .Select(Path.GetFileName)
            .Where(f => f != null).Cast<string>()
            .ToList();
    }

    public HarnessManifest? LoadSnapshot(string snapshotName)
    {
        var path = Path.Combine(ManifestDir, snapshotName);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<HarnessManifest>(File.ReadAllText(path));
    }

    public List<string> Diff(HarnessManifest before, HarnessManifest after)
    {
        var changes = new List<string>();

        if (before.ToolCount != after.ToolCount)
            changes.Add($"Tools: {before.ToolCount} → {after.ToolCount}");

        var beforeMw = string.Join(",", before.MiddlewareChain);
        var afterMw = string.Join(",", after.MiddlewareChain);
        if (beforeMw != afterMw)
            changes.Add($"Middleware: [{beforeMw}] → [{afterMw}]");

        foreach (var comp in after.Components)
        {
            var prev = before.Components.FirstOrDefault(c => c.Name == comp.Name);
            if (prev == null)
                changes.Add($"New component: {comp.Name} ({comp.Type})");
            else if (prev.Hash != comp.Hash)
                changes.Add($"Changed: {comp.Name} ({prev.Hash[..8]} → {comp.Hash[..8]})");
        }

        foreach (var comp in before.Components)
        {
            if (!after.Components.Any(c => c.Name == comp.Name))
                changes.Add($"Removed: {comp.Name}");
        }

        return changes;
    }

    private static string ComputeHash(string input)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }
}
