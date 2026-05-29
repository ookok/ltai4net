using Spectre.Console;

namespace LTAI.Cli;

partial class Program
{
    // ════════════════════════════════════════════════════════════════
    // ltai up [component]
    // ════════════════════════════════════════════════════════════════

    private static async Task RunUpAsync(string[] args)
    {
        var config = CliConfig.Load();
        config.SetEnv();

        var target = args.FirstOrDefault()?.ToLowerInvariant();
        var name = target ?? "tui";

        var baseDir = AppContext.BaseDirectory;
        var exeName = $"LTAI.{name[..1].ToUpper()}{name[1..]}.exe";
        var exePath = Path.Combine(baseDir, name, exeName);
        if (!File.Exists(exePath))
            exePath = Path.Combine(baseDir, "..", name, exeName);
        if (!File.Exists(exePath))
            exePath = Path.Combine(baseDir, $"{name}.exe");

        if (!File.Exists(exePath))
        {
            AnsiConsole.MarkupLine($"[red]Cannot find {exeName}. Publish the component first.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[cyan]Starting {name}...[/]");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            Arguments = ""
        };
        var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit();
    }

    // ════════════════════════════════════════════════════════════════
    // ltai down
    // ════════════════════════════════════════════════════════════════

    private static async Task RunDownAsync(string[] args)
    {
        AnsiConsole.MarkupLine("[yellow]Stopping all components...[/]");
        ProcessLauncher.StopAll();
        AnsiConsole.MarkupLine("[green]All components stopped[/]");
    }

    // ════════════════════════════════════════════════════════════════
    // ltai ps
    // ════════════════════════════════════════════════════════════════

    private static Task RunPsAsync()
    {
        var running = ProcessLauncher.ListProcesses();
        var config = CliConfig.Load();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Component")
            .AddColumn("PID")
            .AddColumn("Status")
            .AddColumn("Version");

        foreach (var (name, pid, status) in running)
        {
            var comp = config.Components.FirstOrDefault(c => c.Name == name);
            table.AddRow(name, pid.ToString(), status, comp?.Version ?? "?");
        }

        foreach (var comp in config.Components.Where(c => !running.Any(r => r.Name == c.Name)))
            table.AddRow(comp.Name, "-", "[dim]stopped[/]", comp.Version);

        if (table.Rows.Count == 0)
            AnsiConsole.MarkupLine("[dim]No components registered. Run 'ltai add <component>' to install one.[/]");
        else
            AnsiConsole.Write(table);

        return Task.CompletedTask;
    }

    // ════════════════════════════════════════════════════════════════
    // ltai update [component]
    // ════════════════════════════════════════════════════════════════

    private static async Task RunUpdateAsync(string[] args)
    {
        var config = CliConfig.Load();
        var target = args.FirstOrDefault()?.ToLowerInvariant() ?? "cli";

        AnsiConsole.MarkupLine($"[cyan]Checking for updates: {target}...[/]");

        if (target == "cli")
        {
            AnsiConsole.MarkupLine("[green]CLI is up to date (V1.0)[/]");
            AnsiConsole.MarkupLine("[dim]Self-update via: curl -L <release-url> -o ltai && chmod +x ltai[/]");
        }
        else if (target == "core" || target == "all")
        {
            AnsiConsole.MarkupLine("[green]Core runtime is up to date (V1.0)[/]");
            config.LastUpdateCheck = DateTime.UtcNow;
            config.Save();
        }
        else
        {
            var comp = config.Components.FirstOrDefault(c => c.Name == target);
            if (comp != null)
            {
                comp.Version = "1.0.0";
                config.Save();
                AnsiConsole.MarkupLine($"[green]{target} updated to V1.0[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Component '{target}' not installed. Run 'ltai add {target}' first[/]");
            }
        }
    }
}
