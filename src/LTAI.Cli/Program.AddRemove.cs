using Spectre.Console;

namespace LTAI.Cli;

partial class Program
{
    // ════════════════════════════════════════════════════════════════
    // ltai add <component>
    // ════════════════════════════════════════════════════════════════

    private static async Task RunAddAsync(string[] args)
    {
        var config = CliConfig.Load();
        var available = new[] { "tui", "desktop", "webapi", "mcp", "webapp" };
        var component = args.FirstOrDefault()?.ToLowerInvariant();

        if (string.IsNullOrEmpty(component) || !available.Contains(component))
        {
            AnsiConsole.MarkupLine("[red]Usage: ltai add <component>[/]");
            AnsiConsole.MarkupLine("Available: tui, desktop, webapi, mcp, webapp");
            return;
        }

        var compDir = Path.Combine(config.InstallPath, component);
        Directory.CreateDirectory(compDir);

        AnsiConsole.MarkupLine($"[cyan]Installing {component}...[/]");

        config.Components.RemoveAll(c => c.Name == component);
        config.Components.Add(new InstalledComponent
        {
            Name = component, Version = "1.0.0",
            Path = compDir, Type = component
        });
        config.Save();

        AnsiConsole.MarkupLine($"[green]{component} installed to {compDir}[/]");
    }

    // ════════════════════════════════════════════════════════════════
    // ltai remove <component>
    // ════════════════════════════════════════════════════════════════

    private static async Task RunRemoveAsync(string[] args)
    {
        var config = CliConfig.Load();
        var component = args.FirstOrDefault()?.ToLowerInvariant();

        if (string.IsNullOrEmpty(component))
        {
            AnsiConsole.MarkupLine("[red]Usage: ltai remove <component>[/]");
            return;
        }

        ProcessLauncher.Stop(component);
        config.Components.RemoveAll(c => c.Name == component);
        config.Save();
        AnsiConsole.MarkupLine($"[green]{component} removed[/]");
    }
}
