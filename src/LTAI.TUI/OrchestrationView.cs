using System.Text;
using LTAI.Agent.Tools;
using Spectre.Console;

namespace LTAI.TUI;

public sealed class OrchestrationView
{
    private readonly SubagentTools? _subagentTools;

    public OrchestrationView(SubagentTools? subagentTools = null)
    {
        _subagentTools = subagentTools;
    }

    public void Render()
    {
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule("[bold]编配中心 Orchestration[/]") { Style = Style.Parse("bold") });
            AnsiConsole.MarkupLine("[dim]commands: plan, agents, q=quit[/]\n");

            var input = AnsiConsole.Ask<string>("\n[grey]>[/] ").Trim();
            if (input is "q" or "quit" or "exit") break;

            var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var cmd = parts[0].ToLowerInvariant();
            var arg = parts.Length > 1 ? parts[1] : "";

            switch (cmd)
            {
                case "plan":
                    RenderPlanStatus();
                    break;
                case "agents":
                    RenderSubAgents();
                    break;
                default:
                    AnsiConsole.MarkupLine($"[red]unknown: {cmd}[/]");
                    break;
            }
            AnsiConsole.MarkupLine("[grey]press any key...[/]");
            Console.ReadKey(true);
        }
    }

    private void RenderPlanStatus()
    {
        var status = "Plan tools: static class — active if plan is running";
        AnsiConsole.Write(new Panel(new Markup(status.EscapeMarkup()))
            .Header("[bold]Plan Status[/]").Border(BoxBorder.Rounded).Expand());
    }

    private void RenderSubAgents()
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Agent");
        table.AddColumn("Status");
        table.AddColumn("Messages");
        table.AddColumn("Budget");
        // SubagentTools doesn't have a list method in the public API,
        // so we show available info
        table.AddRow("[grey]use /agents list[/]", "", "", "");
        AnsiConsole.Write(table);
    }
}
