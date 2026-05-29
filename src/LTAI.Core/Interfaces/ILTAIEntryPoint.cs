namespace LTAI.Core.Interfaces;

/// <summary>
/// CLI entry point interface — any sub-system (Host/MCP/TUI/WebApp) can
/// register itself as an entry point. The CLI dispatcher calls CanHandle
/// then RunAsync on the first matching entry point.
/// Implementations: HostEntryPoint (LTAI.Host), McpEntryPointAdapter (LTAI.MCP),
/// TuiEntryPoint (LTAI.TUI), WebAppEntryPoint (LTAI.WebApp).
/// Callers: LTAI.Cli.Program.
/// </summary>
public interface ILTAIEntryPoint
{
    bool CanHandle(string command);
    Task RunAsync(string[] args);
}
