using System.Reflection;
using Spectre.Console;
using LTAI.Core;
using LTAI.Knowledge.Core;

namespace LTAI.Cli;

partial class Program
{
    public static int Main(string[] args)
    {
        AnsiConsole.Write(new FigletText("LTAI CLI").Color(Color.Green));
        AnsiConsole.MarkupLine("[grey]LivingTree AI — MS Agent Framework 1.8.0[/]");

        if (args.Length == 0)
        {
            ShowHelp();
            AnsiConsole.MarkupLine("\n[grey]Press any key to exit...[/]");
            System.Console.ReadKey(true);
            return 0;
        }

        return args[0].ToLowerInvariant() switch
        {
            "env" => ShowEnv(),
            "version" or "--version" or "-v" => ShowVersion(),
            _ => ShowHelp()
        };
    }

    private static int ShowHelp()
    {
        var table = new Table();
        table.AddColumn("Command");
        table.AddColumn("Description");
        table.AddRow("env", "Show environment variables");
        table.AddRow("version", "Show version");
        AnsiConsole.Write(table);
        return 0;
    }

    private static int ShowEnv()
    {
        var providers = new[] { "DEEPSEEK", "OPENAI", "ANTHROPIC", "GEMINI" };
        AnsiConsole.MarkupLine("[bold]Provider API Keys[/]");
        foreach (var p in providers)
        {
            var val = Environment.GetEnvironmentVariable($"{p}_API_KEY");
            var status = !string.IsNullOrEmpty(val) ? "[green]✓[/]" : "[red]✗[/]";
            AnsiConsole.MarkupLine($"{status} {p}: {(val != null ? $"set ({val[..Math.Min(8, val.Length)]}...)" : "not set")}");
        }
        return 0;
    }

    private static int ShowVersion()
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        AnsiConsole.MarkupLine($"[bold]LTAI CLI[/] v{ver}");
        AnsiConsole.MarkupLine("[grey]Agent Framework: Microsoft.Agents.AI 1.8.0[/]");
        return 0;
    }
}
