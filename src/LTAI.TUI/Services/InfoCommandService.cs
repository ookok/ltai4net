using System.Text;
using LTAI.AI;
using LTAI.Core.Commands;
using LTAI.Core.Configuration;

namespace LTAI.TUI.Services;

public sealed class InfoCommandService : ICommandService
{
    public Task<CommandResult> ExecuteAsync(Command command) => command switch
    {
        HelpCommand => Task.FromResult(HandleHelp()),
        StatusCommand => Task.FromResult(HandleStatus()),
        _ => Task.FromResult<CommandResult>(new SuccessResult("ok")),
    };

    private static CommandResult HandleHelp()
    {
        var groups = SlashCommands.Commands.GroupBy(c => c.Group);
        var lines = new List<string>
        {
            "[bold yellow]┌─────────────────────────────────────┐[/]",
            "[bold yellow]│         LTAI 命令列表                │[/]",
            "[bold yellow]└─────────────────────────────────────┘[/]",
            ""
        };
        foreach (var g in groups.OrderBy(x => x.Key))
        {
            lines.Add($"[bold]{g.Key}[/]");
            lines.Add("[grey]──[/]");
            foreach (var c in g.OrderBy(x => x.Cmd))
            {
                var usageCount = SlashCommands.UsageCount;
                var freq = usageCount.GetValueOrDefault(c.Cmd) > 0
                    ? $" [grey]({usageCount[c.Cmd]}x)[/]" : "";
                var hint = string.IsNullOrEmpty(c.ArgsHint) ? "" : $" [dim]{c.ArgsHint}[/]";
                lines.Add($"  [cyan]/{c.Cmd,-10}[/]{hint,-18} {c.Summary}{freq}");
            }
            lines.Add("");
        }
        lines.Add("[dim]提示: 输入 [yellow]/[/] 打开交互式命令选择器   |   ↑↓ 历史导航[/]");
        return new SuccessResult(string.Join("\n", lines));
    }

    private static CommandResult HandleStatus()
    {
        return new SuccessResult(
            $"[bold]LTAI 状态[/]\n"
            + $"模型: {UsageTracker.ActiveModel}\n"
            + $"提供商: {string.Join(", ", MultiProviderChatClient.DefaultProviders.Select(p => p.name).Take(3))}...\n"
            + $"目录: {Directory.GetCurrentDirectory()}\n"
            + $"Token: {UsageTracker.TotalTokens:N0} | 请求: {UsageTracker.Requests} | 费用: {UsageTracker.CostDisplay}\n"
            + $"缓存: {UsageTracker.CacheHitRate:F1}% | 上下文: {UsageTracker.ContextText()}");
    }
}
