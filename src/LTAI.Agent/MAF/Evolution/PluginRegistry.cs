using System.Text.Json;
using System.Text.Json.Serialization;

namespace LTAI.Agent.Evolution;

public sealed class PluginManifest
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("version")] public string Version { get; init; } = "1.0";
    [JsonPropertyName("type")] public string Type { get; init; } = "agent_bundle";
    [JsonPropertyName("description")] public string? Description { get; init; }

    [JsonPropertyName("agents")] public List<string> Agents { get; init; } = new();
    [JsonPropertyName("tools")] public List<string> Tools { get; init; } = new();
    [JsonPropertyName("mcp_servers")] public List<string> McpServers { get; init; } = new();
    [JsonPropertyName("skills")] public List<string> Skills { get; init; } = new();
    [JsonPropertyName("triggers")] public List<string> Triggers { get; init; } = new();
    [JsonPropertyName("commands")] public List<string> Commands { get; init; } = new();

    [JsonPropertyName("author")] public string? Author { get; init; }
    [JsonPropertyName("license")] public string? License { get; init; }

    public static PluginManifest Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Plugin manifest not found: {path}");
        return JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"Invalid plugin manifest: {path}");
    }
}

public sealed class PluginRegistry
{
    private static readonly string PluginsDir = Path.Combine(".livingtree", "plugins");
    private readonly List<PluginManifest> _plugins = new();
    private readonly Dictionary<string, List<string>> _triggerMap = new();

    public IReadOnlyList<PluginManifest> Plugins { get { lock (_plugins) return _plugins.ToList().AsReadOnly(); } }

    public PluginRegistry()
    {
        Directory.CreateDirectory(PluginsDir);
        Discover();
    }

    public void Discover()
    {
        lock (_plugins)
        {
            _plugins.Clear();
            _triggerMap.Clear();

            if (!Directory.Exists(PluginsDir)) return;

            foreach (var dir in Directory.GetDirectories(PluginsDir))
            {
                var manifestPath = Path.Combine(dir, "plugin.json");
                if (!File.Exists(manifestPath)) continue;

                try
                {
                    var plugin = PluginManifest.Load(manifestPath);
                    _plugins.Add(plugin);

                    foreach (var trigger in plugin.Triggers)
                    {
                        var key = trigger.ToLowerInvariant();
                        if (!_triggerMap.ContainsKey(key))
                            _triggerMap[key] = new();
                        _triggerMap[key].Add(plugin.Name);
                    }
                }
                catch { }
            }
        }
    }

    public PluginManifest? FindByName(string name)
    {
        lock (_plugins) { return _plugins.FirstOrDefault(p => p.Name == name); }
    }

    public List<string> FindByTrigger(string query)
    {
        var matches = new List<string>();
        lock (_plugins)
        {
            foreach (var (trigger, plugins) in _triggerMap)
            {
                if (query.Contains(trigger, StringComparison.OrdinalIgnoreCase))
                    matches.AddRange(plugins);
            }
        }
        return matches.Distinct().ToList();
    }

    public void Install(string pluginName, PluginManifest manifest)
    {
        var dir = Path.Combine(PluginsDir, pluginName);
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(dir, "plugin.json"), json);
        Discover();
    }
}
