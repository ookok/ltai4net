using Spectre.Console;
using LTAI.Agent;

namespace LTAI.Cli;

partial class Program
{
    internal static int HandleAgents(string[] subArgs)
    {
        if (subArgs.Length == 0)
        {
            AnsiConsole.MarkupLine("[bold]Usage:[/] ltai agents list | ltai agents show <name>");
            return ShowAgentsList();
        }
        return subArgs[0].ToLowerInvariant() switch
        {
            "list" or "ls" => ShowAgentsList(),
            "show" or "info" => ShowAgentsDetail(subArgs.Length > 1 ? subArgs[1] : null),
            _ => ShowAgentsDetail(subArgs[0]),
        };
    }

    private static int ShowAgentsList()
    {
        var defs = AgentRegistry.LoadAll();
        if (defs.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No agents/*.agent.md files found.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold]🤖 Registered agents[/] [grey]({defs.Count})[/]\n");

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[bold]Name[/]"); table.AddColumn("[bold]Model[/]"); table.AddColumn("[bold]Temp[/]");
        table.AddColumn("[bold]Tools[/]"); table.AddColumn("[bold]Perms[/]"); table.AddColumn("[bold]Description[/]");

        foreach (var d in defs.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            var perms = string.Join("", d.Permissions.Select(p => p switch
            {
                "read" => "[green]R[/]", "write" => "[yellow]W[/]",
                "list" => "[blue]L[/]", "exec" => "[red]X[/]",
                _ => $"[grey]{p[0]}[/]",
            }));
            table.AddRow($"[bold]{d.Name.EscapeMarkup()}[/]", d.ModelId ?? "[grey]default[/]",
                d.Temperature.ToString("F1"), d.Tools.Length.ToString(), perms,
                (d.Description.Length > 60 ? d.Description[..57] + "..." : d.Description).EscapeMarkup());
        }
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("\n[grey]Perms:[/] [green]R[/]=read [yellow]W[/]=write [blue]L[/]=list [red]X[/]=exec");
        AnsiConsole.MarkupLine("[grey]'ltai agents show <name>' for details.[/]");
        return 0;
    }

    private static int ShowAgentsDetail(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) { Error("Usage: ltai agents show <name>"); return 1; }

        var defs = AgentRegistry.LoadAll();
        var match = defs.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            Error($"Agent '{name}' not found");
            AnsiConsole.MarkupLine($"[grey]Available: {string.Join(", ", defs.Select(d => d.Name))}[/]");
            return 1;
        }

        var permTable = new Table().Border(TableBorder.Rounded).Title("[bold]Permissions[/]");
        permTable.AddColumn("Flag"); permTable.AddColumn("Granted");
        foreach (var p in new[] { "read", "write", "list", "exec" })
            permTable.AddRow(p, match.Permissions.Contains(p) ? "[green]✓[/]" : "[grey]—[/]");

        var toolTable = new Table().Border(TableBorder.Rounded).Title($"[bold]Tools[/] [grey]({match.Tools.Length})[/]");
        toolTable.AddColumn("#"); toolTable.AddColumn("Name");
        for (int i = 0; i < match.Tools.Length; i++) toolTable.AddRow((i + 1).ToString(), match.Tools[i].EscapeMarkup());

        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow(new Panel(permTable).Header($"[bold]{match.Name.EscapeMarkup()}[/]").BorderColor(Color.Green),
                    new Panel(toolTable).Header("[bold]Tools[/]").BorderColor(Color.Blue));

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Markup($"[bold]🤖 {match.Name.EscapeMarkup()}[/]\n"));
        AnsiConsole.MarkupLine($"[grey]Description:[/] {match.Description.EscapeMarkup()}");
        AnsiConsole.MarkupLine($"[grey]Model:[/] [bold]{match.ModelId ?? "default"}[/]  Temp: {match.Temperature:F1}  TopP: {match.TopP:F2}");
        if (!string.IsNullOrWhiteSpace(match.InheritTools))
            AnsiConsole.MarkupLine($"[grey]Inherits:[/] {match.InheritTools.EscapeMarkup()}");
        AnsiConsole.WriteLine();
        AnsiConsole.Write(grid);
        if (!string.IsNullOrWhiteSpace(match.Prompt))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Panel(match.Prompt.EscapeMarkup())
                .Header("[bold]System prompt[/]").BorderColor(Color.Yellow).Expand());
        }
        return 0;
    }
}
