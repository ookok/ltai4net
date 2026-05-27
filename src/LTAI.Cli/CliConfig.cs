using System.Text.Json;

namespace LTAI.Cli;

public sealed class CliConfig
{
    public string InstallPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ltai");
    public string ReleaseChannel { get; set; } = "stable";
    public string L0Endpoint { get; set; } = "http://localhost:11434";
    public string L1ApiKey { get; set; } = "";
    public string L2ApiKey { get; set; } = "";
    public string L2Endpoint { get; set; } = "https://api.deepseek.com";
    public string WorkspaceRoot { get; set; } = "";
    public string SandboxRoot { get; set; } = "";
    public List<InstalledComponent> Components { get; set; } = new();
    public DateTime LastUpdateCheck { get; set; }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ltai", "config.json");

    public static CliConfig Load()
    {
        var path = ConfigPath;
        if (File.Exists(path))
            return JsonSerializer.Deserialize<CliConfig>(File.ReadAllText(path)) ?? new CliConfig();
        return new CliConfig();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, Options));
    }

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
