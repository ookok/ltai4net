namespace LTAI.Core.Configuration;

public sealed class McpConfig
{
    public McpServerConfig[] Servers { get; init; } = Array.Empty<McpServerConfig>();
}

public sealed class McpServerConfig
{
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string[] Args { get; init; } = Array.Empty<string>();
    public Dictionary<string, string>? Env { get; init; }
}

public sealed class MirrorConfig
{
    public string WarpMsiUrl { get; init; } = "http://mogoo.com.cn/Cloudflare_WARP_2026.4.1390.0.msi";
    public string WindowsTerminalUrl { get; init; } = "http://mogoo.com.cn/Microsoft.WindowsTerminal_1.24.11321.0_x64.zip";
    public string RipGrepUrl { get; init; } = "http://mogoo.com.cn/rg.exe";
    public string ModelBaseUrl { get; init; } = "http://mogoo.com.cn/";
}

public sealed class DurableConfig
{
    public bool Enabled { get; init; } = true;
    public int? SidecarPort { get; init; }
    public string DatabasePath { get; init; } = ".livingtree/durability.db";
}

public sealed class HarnessProfile
{
    public string Name { get; set; } = "development";
    public int MaxConcurrentWorkflows { get; set; } = 4;
    public string? SandboxType { get; set; }
    public bool EnableAuditTrail { get; set; } = true;
}
