using System.Text.Json;

namespace LTAI.Cli;

public sealed class CliConfig
{
    public string InstallPath { get; set; } = AppContext.BaseDirectory;
    public string ReleaseChannel { get; set; } = "stable";
    public string L0Endpoint { get; set; } = "http://localhost:11434";
    public string L1ApiKey { get; set; } = "";
    public string L2ApiKey { get; set; } = "";
    public string L2Endpoint { get; set; } = "https://api.deepseek.com";
    public string WorkspaceRoot { get; set; } = Directory.GetCurrentDirectory();
    public string SandboxRoot { get; set; } = "";
    public List<InstalledComponent> Components { get; set; } = new();
    public DateTime LastUpdateCheck { get; set; }

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ltai", "config.json");

    public static CliConfig Load()
    {
        var path = ConfigPath;
        if (!File.Exists(path)) return new CliConfig();

        try
        {
            var text = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var defaults = new CliConfig();
            return new CliConfig
            {
                InstallPath = ReadString(root, "installPath") ?? defaults.InstallPath,
                WorkspaceRoot = ReadString(root, "workspaceRoot") ?? defaults.WorkspaceRoot,
                ReleaseChannel = ReadString(root, "releaseChannel") ?? defaults.ReleaseChannel,
                L0Endpoint = ReadString(root, "l0Endpoint") ?? defaults.L0Endpoint,
                L1ApiKey = ReadString(root, "l1ApiKey") ?? defaults.L1ApiKey,
                L2Endpoint = ReadString(root, "l2Endpoint") ?? defaults.L2Endpoint,
                L2ApiKey = ReadString(root, "l2ApiKey") ?? defaults.L2ApiKey,
                SandboxRoot = ReadString(root, "sandboxRoot") ?? defaults.SandboxRoot,
                LastUpdateCheck = root.TryGetProperty("lastUpdateCheck", out var d)
                    ? DateTime.TryParse(d.GetString(), out var dt) ? dt : defaults.LastUpdateCheck
                    : defaults.LastUpdateCheck
            };
        }
        catch { return new CliConfig(); }
    }

    private static string? ReadString(JsonElement root, string key) =>
        root.TryGetProperty(key, out var el) ? el.GetString() : null;

    public void Save()
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);
        // 手动序列化以绕过 JsonSerializerIsReflectionEnabledByDefault=false
        var json = $$"""
{
  "installPath": "{{EscapeJson(InstallPath)}}",
  "workspaceRoot": "{{EscapeJson(WorkspaceRoot)}}",
  "releaseChannel": "{{EscapeJson(ReleaseChannel)}}",
  "l0Endpoint": "{{EscapeJson(L0Endpoint)}}",
  "l1ApiKey": "{{EscapeJson(L1ApiKey)}}",
  "l2Endpoint": "{{EscapeJson(L2Endpoint)}}",
  "l2ApiKey": "{{EscapeJson(L2ApiKey)}}",
  "sandboxRoot": "{{EscapeJson(SandboxRoot)}}",
  "lastUpdateCheck": "{{LastUpdateCheck:O}}"
}
""";
        File.WriteAllText(ConfigPath, json);
    }

    private static string EscapeJson(string? s) =>
        (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

    public void SetEnv()
    {
        if (!string.IsNullOrEmpty(InstallPath))
            Environment.SetEnvironmentVariable("LTAI_HOME", InstallPath);
        if (!string.IsNullOrEmpty(WorkspaceRoot))
            Environment.SetEnvironmentVariable("LTAI_WORKSPACE", WorkspaceRoot);
        if (!string.IsNullOrEmpty(L1ApiKey))
            Environment.SetEnvironmentVariable("LTAI_L1_API_KEY", L1ApiKey);
        if (!string.IsNullOrEmpty(L2ApiKey))
            Environment.SetEnvironmentVariable("LTAI_L2_API_KEY", L2ApiKey);
    }
}

public sealed class InstalledComponent
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Path { get; set; } = "";
    public string Type { get; set; } = ""; // core, tui, desktop, webapi, mcp, webapp
    public DateTime InstalledAt { get; set; } = DateTime.UtcNow;
}
