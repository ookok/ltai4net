using Spectre.Console;

namespace LTAI.Cli;

partial class Program
{
    internal static int HandleWorkflow(string[] subArgs)
    {
        if (subArgs.Length == 0) return ShowWorkflowHelp();
        return subArgs[0].ToLowerInvariant() switch
        {
            "list" or "ls" => ShowWorkflowList(),
            "reload" => ReloadWorkflows(),
            "show" => ShowWorkflowDetail(subArgs.ElementAtOrDefault(1)),
            _ => ShowWorkflowHelp()
        };
    }

    private static int ShowWorkflowHelp()
    {
        AnsiConsole.MarkupLine("[bold]Usage:[/]");
        AnsiConsole.MarkupLine("  [green]ltai workflow list[/]      — Scan workflow YAML/JSON files");
        AnsiConsole.MarkupLine("  [green]ltai workflow reload[/]    — Trigger hot-reload");
        AnsiConsole.MarkupLine("  [green]ltai workflow show <name>[/] — Show workflow file content");
        return 0;
    }

    private static int ShowWorkflowList()
    {
        var dirs = new[] {
            Path.Combine(Directory.GetCurrentDirectory(), ".livingtree", "workflows"),
            Path.Combine(AppContext.BaseDirectory, "LTAI.Agent.Workflows.ltai-workflows"),
        };

        var files = new List<string>();
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            files.AddRange(Directory.GetFiles(dir, "*.yaml"));
            files.AddRange(Directory.GetFiles(dir, "*.yml"));
            files.AddRange(Directory.GetFiles(dir, "*.json"));
        }

        if (files.Count == 0) { AnsiConsole.MarkupLine("[yellow]No workflow files found.[/]"); return 0; }

        var table = new Table().Border(TableBorder.Rounded).Title($"[bold]Workflows[/] [grey]({files.Count})[/]");
        table.AddColumn("Name"); table.AddColumn("Type"); table.AddColumn("Path");
        foreach (var f in files.OrderBy(f => f))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            var ext = Path.GetExtension(f).ToLowerInvariant();
            var type = ext switch { ".yaml" or ".yml" => "YAML", ".json" => "JSON", _ => ext };
            table.AddRow(name.EscapeMarkup(), type, Path.GetRelativePath(Directory.GetCurrentDirectory(), f).EscapeMarkup());
        }
        AnsiConsole.Write(table);
        return 0;
    }

    private static int ReloadWorkflows()
    {
        // Signal TUI/Web to reload if running; for CLI-only we just rescan
        AnsiConsole.MarkupLine("[green]✅ Workflow scan complete.[/]");
        return ShowWorkflowList();
    }

    private static int ShowWorkflowDetail(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) { Error("Usage: ltai workflow show <name>"); return 1; }
        var dirs = new[] {
            Path.Combine(Directory.GetCurrentDirectory(), ".livingtree", "workflows"),
            Path.Combine(AppContext.BaseDirectory, "LTAI.Agent.Workflows.ltai-workflows"),
        };

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var ext in new[] { "*.yaml", "*.yml", "*.json" })
            {
                var file = Directory.GetFiles(dir, ext).FirstOrDefault(f =>
                    Path.GetFileNameWithoutExtension(f).Equals(name, StringComparison.OrdinalIgnoreCase));
                if (file == null) continue;

                var content = File.ReadAllText(file);
                AnsiConsole.Write(new Panel(content.EscapeMarkup())
                    .Header($"[bold]{Path.GetFileName(file)}[/]").BorderColor(Color.Yellow).Expand());
                return 0;
            }
        }

        Error($"Workflow '{name}' not found.");
        return 1;
    }
}
