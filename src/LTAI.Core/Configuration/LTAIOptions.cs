namespace LTAI.Core.Configuration;

/// <summary>
/// Root configuration object, loaded from appsettings.json under section "LTAI".
/// Holds AI, Web, Vector, and Harness sub-configs plus runtime directory resolution.
/// Validated at startup by <see cref="LTAIOptionsValidator"/>.
/// </summary>
public sealed class LTAIOptions
{
    public const string SectionName = "LTAI";
    public WorkflowsConfig Workflows { get; init; } = new();
    public SessionConfig Session { get; init; } = new();
    public AIConfig AI { get; init; } = new();
    public WebConfig Web { get; init; } = new();
    public VectorConfig Vector { get; init; } = new();
    public HarnessProfile Harness { get; set; } = new();
    public McpConfig Mcp { get; init; } = new();
    public DurableConfig Durable { get; init; } = new();
    public AutoTuneConfig AutoTune { get; init; } = new();
    public EmbeddingConfig Embedding { get; init; } = new();
    public SteerConfig Steer { get; init; } = new();
    public MirrorConfig Mirrors { get; init; } = new();
    public SecurityConfig Security { get; init; } = new();
    public ToolTrustConfig ToolTrust { get; init; } = new();
    public ProviderDefinition[] Providers { get; init; } = [];
    public string DataDirectory { get; init; } = ".livingtree";
    public string ToolsDirectory { get; init; } = "tools";
    public string[] SkillsUrls { get; init; } = Array.Empty<string>();
    public string PromptsDirectory { get; init; } = "prompts";
    public string MemoryDirectory { get; init; } = "memory";
    public string ModelsDirectory { get; init; } = "models";
    public string LogsDirectory { get; init; } = "logs";
    public int MaxHistoryMessages { get; init; } = 200;
    public bool EnableObservability { get; init; } = false;

    public string ResolveDataPath(string subPath) =>
        Path.Combine(EnvDataDir ?? AppContext.BaseDirectory, DataDirectory, subPath);

    public string ResolveToolsPath(string? subPath = null) =>
        Path.Combine(EnvToolsDir ?? AppContext.BaseDirectory, ToolsDirectory, subPath ?? "");

    public string ResolvePromptsPath(string? subPath = null) =>
        Path.Combine(EnvPromptsDir ?? AppContext.BaseDirectory, PromptsDirectory, subPath ?? "");

    public string ResolveMemoryPath(string? subPath = null) =>
        Path.Combine(EnvMemoryDir ?? AppContext.BaseDirectory, MemoryDirectory, subPath ?? "");

    private static readonly string? EnvDataDir = Environment.GetEnvironmentVariable("LTAI_DATA_DIR");
    private static readonly string? EnvToolsDir = Environment.GetEnvironmentVariable("LTAI_TOOLS_DIR");
    private static readonly string? EnvPromptsDir = Environment.GetEnvironmentVariable("LTAI_PROMPTS_DIR");
    private static readonly string? EnvMemoryDir = Environment.GetEnvironmentVariable("LTAI_MEMORY_DIR");

    public static void SaveLayerToAppSettings(string layer, string provider, string model)
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var path = Path.Combine(baseDir, "appsettings.json");
            System.Text.Json.Nodes.JsonNode json;
            if (File.Exists(path))
                json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            else
                json = new System.Text.Json.Nodes.JsonObject();
            var ltai = json["LTAI"] as System.Text.Json.Nodes.JsonObject
                       ?? new System.Text.Json.Nodes.JsonObject();
            json["LTAI"] = ltai;
            var ai = ltai["AI"] as System.Text.Json.Nodes.JsonObject
                     ?? new System.Text.Json.Nodes.JsonObject();
            ltai["AI"] = ai;
            var existingKey = ((System.Collections.Generic.IDictionary<string, System.Text.Json.Nodes.JsonNode?>)ai)
                .Keys.FirstOrDefault(k => string.Equals(k, layer, StringComparison.OrdinalIgnoreCase));
            if (existingKey != null) ai.Remove(existingKey);
            ai[layer] = new System.Text.Json.Nodes.JsonObject
            {
                ["Provider"] = provider,
                ["Model"] = model
            };
            File.WriteAllText(path, json.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch { }
    }
}

