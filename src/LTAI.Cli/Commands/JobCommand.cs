using Spectre.Console;
using LTAI.Agent.Tools;

namespace LTAI.Cli;

partial class Program
{
    internal static int HandleJob(string[] subArgs)
    {
        if (subArgs.Length == 0) { ShowJobHelp(); return 0; }
        return subArgs[0].ToLowerInvariant() switch
        {
            "list" or "ls" => ListJobs(),
            "show" => ShowJob(subArgs.ElementAtOrDefault(1)),
            _ => ShowJobHelp()
        };
    }

    private static int ShowJobHelp()
    {
        AnsiConsole.MarkupLine("[bold]Usage:[/]");
        AnsiConsole.MarkupLine("  [green]ltai job list[/]          — List active background jobs");
        AnsiConsole.MarkupLine("  [green]ltai job show <id>[/]     — Show job detail");
        return 0;
    }

    private static int ListJobs()
    {
        var svc = new BackgroundJobService();
        var jobs = svc.SnapshotJobs();
        if (jobs.Count == 0) { AnsiConsole.MarkupLine("[yellow]No active jobs.[/]"); return 0; }

        var table = new Table().Border(TableBorder.Rounded).Title($"[bold]Jobs[/] [grey]({jobs.Count})[/]");
        table.AddColumn("ID"); table.AddColumn("Completed"); table.AddColumn("Command"); table.AddColumn("Age");
        foreach (var (id, j) in jobs.OrderBy(j => j.Value.StartedAtUtc))
        {
            var status = j.Completed ? "[blue]done[/]" : "[green]running[/]";
            var cmdPreview = (j.Command ?? "").Length > 40 ? (j.Command ?? "")[..37] + "..." : (j.Command ?? "");
            table.AddRow(id.EscapeMarkup(), status, cmdPreview.EscapeMarkup(),
                (DateTime.UtcNow - j.StartedAtUtc).ToString(@"mm\:ss"));
        }
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[grey]'ltai job show <id>' for detail.[/]");
        return 0;
    }

    private static int ShowJob(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) { Error("Usage: ltai job show <id>"); return 1; }
        var svc = new BackgroundJobService();
        var entry = svc.GetJobEntry(id);
        if (entry == null) { Error($"Job '{id}' not found."); return 1; }

        AnsiConsole.MarkupLine($"[bold]📋 Job: {id.EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine($"[grey]Started:[/] {entry.StartedAtUtc:u}  [grey]Completed:[/] {entry.Completed}");
        if (!string.IsNullOrEmpty(entry.Command))
            AnsiConsole.MarkupLine($"[grey]Command:[/] {entry.Command.EscapeMarkup()}");
        if (!string.IsNullOrEmpty(entry.Error))
            AnsiConsole.MarkupLine($"[red]Error:[/] {entry.Error.EscapeMarkup()}");
        return 0;
    }
}
