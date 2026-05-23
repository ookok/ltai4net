using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using LTAI.Cli.Model;
using LTAI.Core.Setup;

namespace LTAI.Cli;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        if (command == "setup" || command == "help" || command == "--help" || command == "-h")
        {
            if (command == "setup") return await RunSetupAsync();
            PrintHelp();
            return 0;
        }

        var rootCommand = CreateRootCommand();
        var parser = new CommandLineBuilder(rootCommand).UseDefaults().Build();
        return await parser.InvokeAsync(args);
    }

    private static RootCommand CreateRootCommand()
    {
        var root = new RootCommand("LTAI CLI v7.0 — debug, improve, model management");

        var debugCommand = new Command("debug", "End-to-end tests with full link tracing");
        var queryOpt = new Option<string?>("--query", "Specific query to trace");
        var countOpt = new Option<int>("--count", () => 20, "Number of test cases");
        debugCommand.AddOption(queryOpt); debugCommand.AddOption(countOpt);
        debugCommand.SetHandler(async ctx =>
        {
            await DebugMode.RunAsync(
                ctx.ParseResult.GetValueForOption(queryOpt),
                ctx.ParseResult.GetValueForOption(countOpt),
                ctx.ParseResult.GetValueForOption(new Option<string?>("--difficulty")),
                ctx.ParseResult.GetValueForOption(new Option<string?>("--domain")),
                ctx.ParseResult.GetValueForOption(new Option<bool>("--report")));
        });
        root.AddCommand(debugCommand);

        var improveCommand = new Command("improve", "Architecture audit + paper-driven innovation proposals");
        var autoOpt = new Option<bool>("--auto", () => false, "Run full pipeline");
        improveCommand.AddOption(autoOpt);
        improveCommand.SetHandler(async ctx =>
        {
            var auto = ctx.ParseResult.GetValueForOption(autoOpt);
            await ImproveMode.RunAsync(auto, auto, auto, auto);
        });
        root.AddCommand(improveCommand);

        var modelCommand = new Command("model", "Manage local models");
        var modelArg = new Argument<string?>("command", "list, download, remove, reset");
        modelCommand.AddArgument(modelArg);
        modelCommand.SetHandler(async ctx =>
        {
            var cmd = ctx.ParseResult.GetValueForArgument(modelArg);
            await ModelMode.RunAsync(cmd);
            ctx.ExitCode = 0;
        });
        root.AddCommand(modelCommand);

        return root;
    }

    private static async Task<int> RunSetupAsync()
    {
        var wizard = new InteractiveSetupWizard(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        await wizard.RunAsync();
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("LTAI CLI v7.0");
        Console.WriteLine("  ltai setup     Interactive config wizard");
        Console.WriteLine("  ltai model     Manage local models (list/download/remove)");
        Console.WriteLine("  ltai debug     Run trace tests");
        Console.WriteLine("  ltai improve   Architecture audit + AI paper proposals");
    }
}
