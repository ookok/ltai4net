using System.Reflection;
using Spectre.Console;

namespace LTAI.Cli;

partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        // ── Auto-load .env from solution root or output directory ──
        LTAI.Core.Configuration.DotEnvLoader.Load();

        Console.Title = "LTAI CLI";
        AnsiConsole.Write(new FigletText("LTAI CLI").Color(Color.Green));
        AnsiConsole.MarkupLine("[grey]LivingTree AI — Agent Framework[/] [blue]⚡[/]");

        if (args.Length == 0)
        {
            ShowHelp();
            AnsiConsole.MarkupLine("\n[grey]Press any key to exit...[/]");
            Console.ReadKey(true);
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        return command switch
        {
            "init" or "setup" => await HandleInit().ConfigureAwait(false),
            "env" => HandleEnv(args[1..]),
            "migrate" => await HandleMigrate(args[1..]).ConfigureAwait(false),
            "textpad" => HandleTextPad(args[1..]),
            "dashboard" or "dash" => HandleDashboard(),
            "health" or "--health" or "hc" => await HandleHealth().ConfigureAwait(false),
            "agents" => HandleAgents(args[1..]),
            "workflow" => HandleWorkflow(args[1..]),
            "job" => HandleJob(args[1..]),
            "model" => HandleModel(args[1..]),
            "provider" => HandleProvider(args[1..]),
            "graph" => await HandleGraph(args[1..]).ConfigureAwait(false),
            "mcp-server" or "mcp" => await LTAI.CLI.McpServer.RunAsync(
                Directory.GetCurrentDirectory(), args.ElementAtOrDefault(1) ?? "readonly").ConfigureAwait(false),
            "version" or "--version" or "-v" => ShowVersion(),
            _ => ShowHelp()
        };
    }

    private static int ShowHelp()
    {
        var table = new Table().Border(TableBorder.Rounded).Title("[bold]LTAI CLI — Commands[/]");
        table.AddColumn("Command"); table.AddColumn("Description");

        void Add(string cmd, string desc) => table.AddRow(cmd.EscapeMarkup(), desc.EscapeMarkup());

        Add("init (setup)", "Interactive setup wizard");
        Add("env", "Show / export / import environment variables");
        Add("env get|set <name> <value>", "Get or set a single environment variable");
        Add("migrate", "Check LiteDB → SQLite migration status");
        Add("textpad [path]", "Interactive file browser / editor");
        Add("dashboard (dash)", "Real-time usage dashboard");
        Add("health (hc)", "System health check");
        Add("agents list|show <name>", "List / inspect agent definitions");
        Add("workflow list|reload|show", "Manage YAML hot-reloadable workflows");
        Add("job list|show|cancel <id>", "Manage background jobs");
        Add("model list|switch <name>", "Manage embedding models");
        Add("provider list|set|apikey <name>", "Configure LLM providers");
        Add("graph search|stats [query]", "Search knowledge graph");
        Add("mcp-server [readonly|all]", "Start MCP stdio server for IDE integration");
        Add("version", "Show version");

        AnsiConsole.Write(table);
        return 0;
    }

    private static int ShowVersion()
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        AnsiConsole.MarkupLine($"[bold]LTAI CLI[/] v{ver}");
        AnsiConsole.MarkupLine("[grey]Agent Framework: Microsoft.Agents.AI (git submodule)[/]");
        return 0;
    }

    private static void Error(string msg) => AnsiConsole.MarkupLine($"[red]{msg.EscapeMarkup()}[/]");
}
