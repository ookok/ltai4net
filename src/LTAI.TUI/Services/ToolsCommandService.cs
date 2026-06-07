using System.Text;
using LTAI.AI;
using LTAI.Core.Commands;
using Spectre.Console;

namespace LTAI.TUI.Services;

public sealed class ToolsCommandService : ICommandService
{
    public CommandResult Execute(Command command) => command switch
    {
        ToolsCommand tc => HandleToolsCommand(tc.Args),
        _ => new SuccessResult("ok"),
    };

    private CommandResult HandleToolsCommand(string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";

        if (sub == "domain" && parts.Length > 1)
            return ListToolsByDomain(parts[1]);

        return ListAllTools();
    }

    private static CommandResult ListAllTools()
    {
        var all = ToolRegistry.AllTools;
        if (all.Count == 0)
            return new SuccessResult("[yellow]没有已注册的工具（尚未初始化）[/]");

        var sb = new StringBuilder();
        sb.AppendLine($"[bold yellow]已注册的工具 ({all.Count})[/]\n");

        var groups = all.GroupBy(t => string.IsNullOrEmpty(t.Domain) ? "default" : t.Domain)
            .OrderBy(g => g.Key);

        foreach (var g in groups)
        {
            sb.AppendLine($"[bold]{g.Key.EscapeMarkup()}[/]");
            foreach (var t in g.OrderBy(x => x.Name))
            {
                var desc = t.Description.EscapeMarkup();
                sb.AppendLine($"  · [cyan]{t.Name.EscapeMarkup()}[/] — {desc}");
            }
            sb.AppendLine();
        }
        return new SuccessResult(sb.ToString());
    }

    private static CommandResult ListToolsByDomain(string domain)
    {
        var tools = ToolRegistry.GetToolsByDomain(domain);
        if (tools.Count == 0)
            return new SuccessResult($"[yellow]域 '{domain.EscapeMarkup()}' 中没有已注册的工具[/]");

        var sb = new StringBuilder();
        sb.AppendLine($"[bold yellow]域 '{domain.EscapeMarkup()}' 的工具 ({tools.Count})[/]\n");
        foreach (var t in tools.OrderBy(x => x.Name))
        {
            sb.AppendLine($"  · [cyan]{t.Name.EscapeMarkup()}[/] — {t.Description.EscapeMarkup()}");
        }
        return new SuccessResult(sb.ToString());
    }
}
