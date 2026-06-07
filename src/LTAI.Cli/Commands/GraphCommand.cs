using Spectre.Console;
using LTAI.Agent.Vector;

namespace LTAI.Cli;

partial class Program
{
    internal static async Task<int> HandleGraph(string[] subArgs)
    {
        if (subArgs.Length == 0) return ShowGraphHelp();
        return subArgs[0].ToLowerInvariant() switch
        {
            "stats" => await GraphStats().ConfigureAwait(false),
            _ => ShowGraphHelp()
        };
    }

    private static int ShowGraphHelp()
    {
        AnsiConsole.MarkupLine("[bold]Usage:[/]");
        AnsiConsole.MarkupLine("  [green]ltai graph stats[/]          — Show knowledge graph statistics");
        return 0;
    }

    private static async Task<int> GraphStats()
    {
        var dbPath = Path.Combine(Directory.GetCurrentDirectory(), ".livingtree", "kg.db");
        if (!File.Exists(dbPath)) { Error("No knowledge graph found. Run the app first."); return 1; }

        try
        {
            using var store = new KgStore(dbPath);
            var stats = await store.Stats().ConfigureAwait(false);
            var panel = new Panel(stats.EscapeMarkup()).Header("[bold]📊 Knowledge Graph[/]").BorderColor(Color.Green);
            AnsiConsole.Write(panel);
            return 0;
        }
        catch (Exception ex) { Error($"Stats failed: {ex.Message}"); return 1; }
    }
}
