using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LTAI.Core.Messaging;
using System.Text.Json;

namespace LTAI.Core.Configuration;

public sealed class AppConfiguration
{
    public string DataDirectory { get; init; } = ".livingtree";
    public List<string> PluginDirectories { get; init; } = new();
    public MiddlewarePipelineConfig Middleware { get; init; } = new();
    public List<AgentConfig> Agents { get; init; } = new();
    public List<ToolCategoryConfig> Tools { get; init; } = new();

    public static AppConfiguration Load(string path = "ltai.config.json")
    {
        if (!File.Exists(path)) return new AppConfiguration();
        return JsonSerializer.Deserialize<AppConfiguration>(File.ReadAllText(path)) ?? new();
    }
}

public sealed class MiddlewarePipelineConfig
{
    public List<string> Pipeline { get; init; } = new()
    {
        "prompt_shield", "input_classifier", "dna_safety",
        "tool_governance", "output_review"
    };
    public bool AutoDiscover { get; init; } = true;
    public List<string> Disabled { get; init; } = new();
}

public sealed class AgentConfig
{
    public string Name { get; init; } = "general";
    public string? A2APath { get; init; }
    public string? Instructions { get; init; }
    public List<string> Tools { get; init; } = new();
    public List<string> Middleware { get; init; } = new();
}

public sealed class ToolCategoryConfig
{
    public string Category { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public List<string> DisabledTools { get; init; } = new();
}
