using System.Text;
using LTAI.Agent.Mcp;
using LTAI.Core.Commands;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Options;
using Spectre.Console;

namespace LTAI.TUI.Services;

public sealed class McpCommandService : ICommandService
{
    private readonly McpClientFactory? _mcpFactory;
    private readonly IOptions<LTAIOptions>? _options;

    public McpCommandService(McpClientFactory? mcpFactory, IOptions<LTAIOptions>? options = null)
    {
        _mcpFactory = mcpFactory;
        _options = options;
    }

    public CommandResult Execute(Command command) => command switch
    {
        McpCommand mc => HandleMcpCommand(mc.Args),
        _ => new SuccessResult("ok"),
    };

    private CommandResult HandleMcpCommand(string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";

        return sub switch
        {
            "" or "list" or "status" => ListMcpServers(),
            "tools" => ListMcpTools(),
            _ => new SuccessResult("用法: /mcp list|status|tools"),
        };
    }

    private CommandResult ListMcpServers()
    {
        var config = _options?.Value.Mcp;
        var servers = config?.Servers ?? [];

        var sb = new StringBuilder();
        sb.AppendLine("[bold yellow]MCP 服务器配置[/]\n");

        if (servers.Length == 0)
        {
            sb.AppendLine("[grey]未配置 MCP 服务器[/]");
            sb.AppendLine("[dim]在 appsettings.json 的 LTAI:Mcp:Servers 中配置[/]");
            return new SuccessResult(sb.ToString());
        }

        foreach (var s in servers)
        {
            sb.AppendLine($"  [cyan]{s.Name.EscapeMarkup()}[/]");
            sb.AppendLine($"    命令: [grey]{s.Command.EscapeMarkup()}[/] {string.Join(" ", s.Args.Select(a => a.EscapeMarkup()))}");
            if (s.Env is { Count: > 0 })
                sb.AppendLine($"    环境变量: {string.Join(", ", s.Env.Keys.Select(k => $"[grey]{k.EscapeMarkup()}[/]"))}");
            sb.AppendLine();
        }
        sb.AppendLine($"[grey]共 {servers.Length} 个 MCP 服务器配置[/]");
        return new SuccessResult(sb.ToString());
    }

    private CommandResult ListMcpTools()
    {
        var config = _options?.Value.Mcp;
        if (config == null || config.Servers.Length == 0)
            return new SuccessResult("[yellow]未配置 MCP 服务器，无可用工具[/]");

        var sb = new StringBuilder();
        sb.AppendLine("[bold yellow]MCP 可用工具[/]\n");

        try
        {
            var tools = _mcpFactory?.GetToolsAsync(config, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (tools == null || tools.Count == 0)
            {
                sb.AppendLine("[yellow]暂无可用 MCP 工具，请确认 MCP 服务器已连接[/]");
                return new SuccessResult(sb.ToString());
            }

            foreach (var t in tools.OrderBy(t => t.Name))
            {
                var desc = t.Description?.EscapeMarkup() ?? "";
                var paramsCount = t.AdditionalProperties?.Count ?? 0;
                sb.AppendLine($"  · [cyan]{t.Name.EscapeMarkup()}[/] — {desc}" +
                    (paramsCount > 0 ? $" [dim]({paramsCount} 参数)[/]" : ""));
            }
            sb.AppendLine($"\n[grey]共 {tools.Count} 个 MCP 工具[/]");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[red]获取 MCP 工具失败: {ex.Message.EscapeMarkup()}[/]");
        }

        return new SuccessResult(sb.ToString());
    }
}
