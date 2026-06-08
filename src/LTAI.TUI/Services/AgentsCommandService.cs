using System.Text;
using LTAI.Agent.DevUI;
using LTAI.Core.Commands;
using Spectre.Console;

namespace LTAI.TUI.Services;

public sealed class AgentsCommandService : ICommandService
{
    private readonly LTAIDevUIService _devUi;

    public AgentsCommandService(LTAIDevUIService devUi)
    {
        _devUi = devUi;
    }

    public Task<CommandResult> ExecuteAsync(Command command) => command switch
    {
        AgentsCommand ac => Task.FromResult(HandleAgentsCommand(ac.Args)),
        _ => Task.FromResult<CommandResult>(new SuccessResult("ok")),
    };

    private CommandResult HandleAgentsCommand(string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";

        if (sub == "show" && parts.Length > 1)
            return ShowAgent(parts[1]);

        return ListAgents();
    }

    private CommandResult ListAgents()
    {
        var cards = _devUi.ListAgentCards();
        if (cards.Count == 0)
            return new SuccessResult("[yellow]没有已注册的 Agent[/]");

        var sb = new StringBuilder();
        sb.AppendLine("[bold yellow]已注册的 Agent[/]\n");
        sb.AppendLine($"[grey]{"Name",-16} {"Model",-24} T    Tools  Perms  Description[/]");
        sb.AppendLine($"[grey]{"────",-16} {"─────",-24} ─   ─────  ─────  ───────────[/]");

        foreach (var c in cards.OrderBy(c => c.Name))
        {
            var perms = c.Permissions.Count == 0 ? "—" : string.Join("", c.Permissions.Select(p => p[..1]));
            var model = c.ModelId ?? "—";
            sb.AppendLine($"  [cyan]{c.Name,-14}[/] {model,-22} {c.Temperature,-3:F1}  {c.ToolCount,-4}  {perms,-5}  {c.Description.EscapeMarkup()}");
        }
        sb.AppendLine($"\n[grey]共 {cards.Count} 个 Agent，使用 /agents show <name> 查看详情[/]");
        return new SuccessResult(sb.ToString());
    }

    private CommandResult ShowAgent(string name)
    {
        var card = _devUi.GetAgentCard(name);
        if (card == null)
            return new SuccessResult($"[red]Agent '{name.EscapeMarkup()}' 未找到[/]. 使用 /agents list 查看全部");

        var sb = new StringBuilder();
        sb.AppendLine($"[bold yellow]Agent: {card.Name.EscapeMarkup()}[/]");
        sb.AppendLine($"  描述: {card.Description.EscapeMarkup()}");
        sb.AppendLine($"  版本: {card.Version}");
        sb.AppendLine($"  模型: {card.ModelId?.EscapeMarkup() ?? "[grey]未指定[/]"}");
        sb.AppendLine($"  温度: {card.Temperature:F1}");
        sb.AppendLine($"  TopP: {card.TopP:F2}");
        if (card.Tools.Count > 0)
            sb.AppendLine($"  工具: {string.Join(", ", card.Tools.Select(t => $"[cyan]{t.EscapeMarkup()}[/]"))}");
        if (card.Permissions.Count > 0)
            sb.AppendLine($"  权限: {string.Join(", ", card.Permissions)}");
        if (card.Skills.Count > 0)
        {
            sb.AppendLine($"  技能:");
            foreach (var sk in card.Skills)
                sb.AppendLine($"    · [cyan]{sk.Name.EscapeMarkup()}[/] — {sk.Description?.EscapeMarkup() ?? ""}");
        }
        if (card.Tags.Count > 0)
            sb.AppendLine($"  标签: {string.Join(", ", card.Tags.Select(t => $"[grey]{t.EscapeMarkup()}[/]"))}");
        return new SuccessResult(sb.ToString());
    }
}
