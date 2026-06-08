using System.Text;
using System.Text.Json;
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
        var arg = parts.Length > 1 ? parts[1] : "";

        return (sub, arg) switch
        {
            ("" or "list" or "status", _) => ListMcpServers(),
            ("tools", _) => ListMcpTools(),
            ("add", _) => AddMcpServer(arg),
            ("remove" or "rm" or "delete", _) => RemoveMcpServer(arg),
            ("edit", _) => EditMcpServer(arg),
            _ => new SuccessResult("用法: /mcp list|status|tools|add <name> <command> [args...]|remove <name>|edit <name> <command> [args...]"),
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
            sb.AppendLine("[dim]使用 /mcp add <name> <command> [args...] 添加[/]");
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
        sb.AppendLine("[dim]/mcp add|remove|edit 管理服务器[/]");
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

    private CommandResult AddMcpServer(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
            return new SuccessResult("[yellow]用法: /mcp add <name> <command> [args...][/]");

        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return new SuccessResult("[yellow]用法: /mcp add <name> <command> [args...][/]");

        var name = parts[0];
        var cmdAndArgs = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var command = cmdAndArgs[0];
        var args = cmdAndArgs.Length > 1 ? cmdAndArgs[1..] : [];

        try
        {
            ModifyMcpConfig(servers =>
            {
                if (servers.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"MCP 服务器 '{name}' 已存在");
                return servers.Append(new McpServerConfig
                {
                    Name = name,
                    Command = command,
                    Args = args,
                }).ToArray();
            });
            return new SuccessResult($"[green]已添加 MCP 服务器: {name.EscapeMarkup()} ({command.EscapeMarkup()})[/]");
        }
        catch (Exception ex)
        {
            return new SuccessResult($"[red]{ex.Message.EscapeMarkup()}[/]");
        }
    }

    private CommandResult RemoveMcpServer(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
            return new SuccessResult("[yellow]用法: /mcp remove <name>[/]");

        try
        {
            var removed = false;
            ModifyMcpConfig(servers =>
            {
                var filtered = servers.Where(s => !s.Name.Equals(arg, StringComparison.OrdinalIgnoreCase)).ToArray();
                removed = filtered.Length < servers.Length;
                return filtered;
            });
            return removed
                ? new SuccessResult($"[green]已移除 MCP 服务器: {arg.EscapeMarkup()}[/]")
                : new SuccessResult($"[yellow]未找到 MCP 服务器: {arg.EscapeMarkup()}[/]");
        }
        catch (Exception ex)
        {
            return new SuccessResult($"[red]{ex.Message.EscapeMarkup()}[/]");
        }
    }

    private CommandResult EditMcpServer(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
            return new SuccessResult("[yellow]用法: /mcp edit <name> <newCommand> [args...][/]");

        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return new SuccessResult("[yellow]用法: /mcp edit <name> <newCommand> [args...][/]");

        var name = parts[0];
        var cmdAndArgs = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var command = cmdAndArgs[0];
        var args = cmdAndArgs.Length > 1 ? cmdAndArgs[1..] : [];

        try
        {
            var found = false;
            ModifyMcpConfig(servers =>
            {
                return servers.Select(s =>
                {
                    if (s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        return new McpServerConfig { Name = s.Name, Command = command, Args = args, Env = s.Env };
                    }
                    return s;
                }).ToArray();
            });
            return found
                ? new SuccessResult($"[green]已更新 MCP 服务器: {name.EscapeMarkup()} → {command.EscapeMarkup()}[/]")
                : new SuccessResult($"[yellow]未找到 MCP 服务器: {name.EscapeMarkup()}[/]");
        }
        catch (Exception ex)
        {
            return new SuccessResult($"[red]{ex.Message.EscapeMarkup()}[/]");
        }
    }

    private void ModifyMcpConfig(Func<McpServerConfig[], McpServerConfig[]> transform)
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(configPath))
            configPath = Path.Combine(Environment.CurrentDirectory, "appsettings.json");
        if (!File.Exists(configPath))
            throw new FileNotFoundException("找不到 appsettings.json");

        var json = File.ReadAllText(configPath);
        using var doc = JsonDocument.Parse(json);
        var root = new Dictionary<string, JsonElement?>();
        foreach (var prop in doc.RootElement.EnumerateObject())
            root[prop.Name] = prop.Value.Clone();

        var currentServers = _options?.Value.Mcp.Servers ?? [];
        var newServers = transform(currentServers.ToArray());

        // Build new Mcp element
        var mcpObj = new Dictionary<string, object>
        {
            ["Servers"] = newServers.Select(s => new Dictionary<string, object>
            {
                ["Name"] = s.Name,
                ["Command"] = s.Command,
                ["Args"] = s.Args,
                ["Env"] = s.Env ?? new Dictionary<string, string>(),
            }).ToArray()
        };

        // Navigate to LTAI:Mcp and patch
        var rootObj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        if (rootObj == null) throw new InvalidOperationException("无法解析 appsettings.json");

        PatchJsonPath(rootObj, ["LTAI", "Mcp"], mcpObj);

        var options = new JsonSerializerOptions { WriteIndented = true };
        var newJson = JsonSerializer.Serialize(rootObj, options);
        File.WriteAllText(configPath, newJson);
    }

    private static void PatchJsonPath(Dictionary<string, JsonElement> obj, string[] path, object value)
    {
        if (path.Length == 0) return;
        var key = path[0];
        if (path.Length == 1)
        {
            obj[key] = JsonSerializer.SerializeToElement(value);
            return;
        }

        if (obj.TryGetValue(key, out var existing) && existing.ValueKind == JsonValueKind.Object)
        {
            var nested = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existing.GetRawText());
            if (nested != null)
            {
                PatchJsonPath(nested, path[1..], value);
                obj[key] = JsonSerializer.SerializeToElement(nested);
            }
        }
    }
}
