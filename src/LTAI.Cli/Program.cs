using System.Reflection;
using LTAI.Cli.Commands;
using LTAI.Core.Interfaces;
using LTAI.Core.Setup;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;

namespace LTAI.Cli;

public class Program
{
    private static readonly Dictionary<string, ILTAIEntryPoint> _entryPoints = new();

    public static async Task<int> Main(string[] args)
    {
        ScanEntryPoints();

        if (args.Length > 0)
        {
            var cmd = args[0].ToLowerInvariant();

            if (_entryPoints.TryGetValue(cmd, out var entry))
            {
                await entry.RunAsync(args[1..]);
                return 0;
            }

            if (cmd is "setup") { await RunSetupAsync(); return 0; }
        }

        var services = new ServiceCollection();
        var registrar = new TypeRegistrar(services);

        var app = new CommandApp(registrar);
        app.Configure(config =>
        {
            config.SetApplicationName("ltai");
            config.SetApplicationVersion("V0.51");

            config.Settings.CaseSensitivity = CaseSensitivity.None;
            config.Settings.StrictParsing = false;

            config.SetInterceptor(new CliInterceptor());

            config.SetExceptionHandler((ex, _) =>
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
                if (ex.InnerException != null)
                    AnsiConsole.MarkupLine($"[dim]  → {ex.InnerException.Message}[/]");
                return -1;
            });

#if DEBUG
            config.PropagateExceptions();
            config.ValidateExamples();
#endif

            config.AddCommand<DebugCommand>("debug")
                .WithDescription("End-to-end tests with full link tracing")
                .WithAlias("d")
                .WithExample("debug")
                .WithExample("debug", "--count", "50")
                .WithExample("debug", "--query", "\"What is LTAI?\"");

            config.AddCommand<AutoFixCommand>("auto-fix")
                .WithDescription("LLM-driven debugging with root-cause tracing (no logs needed)")
                .WithAlias("fix")
                .WithExample("auto-fix", "--target", "src/LTAI.Agent")
                .WithExample("auto-fix", "--target", "src/LTAI.Agent", "--analyze")
                .WithExample("auto-fix", "--target", "src/LTAI.Agent", "--attempts", "5")
                .WithExample("auto-fix", "--target", "src/LTAI.Agent/SomeFile.cs", "--scan");

            config.AddCommand<ImproveCommand>("improve")
                .WithDescription("Architecture audit + paper-driven innovation proposals")
                .WithAlias("i")
                .WithExample("improve", "--auto")
                .WithExample("improve", "--scan", "--papers");

            config.AddBranch("model", model =>
            {
                model.SetDescription("Manage local AI models");
                model.AddCommand<ModelListCommand>("list")
                    .WithDescription("List available models")
                    .WithAlias("ls")
                    .WithExample("model", "list")
                    .WithExample("model", "list", "--layer", "L1");
                model.AddCommand<ModelDownloadCommand>("download")
                    .WithDescription("Download a model")
                    .WithAlias("dl")
                    .WithExample("model", "download", "--version", "qwen2.5-1.5b-q4")
                    .WithExample("model", "download", "--layer", "L1", "--version", "qwen2.5-1.5b-q4", "--mirror");
                model.AddCommand<ModelRemoveCommand>("remove")
                    .WithDescription("Remove an installed model")
                    .WithAlias("rm")
                    .WithExample("model", "remove", "--version", "qwen2.5-1.5b-q4");
                model.AddCommand<ModelResetCommand>("reset")
                    .WithDescription("Remove all models and clear config")
                    .WithExample("model", "reset")
                    .WithExample("model", "reset", "--setup");
            });

            config.AddBranch("skill", skill =>
            {
                skill.SetDescription("Manage skills — the only distributable artifact");
                skill.AddCommand<SkillListCommand>("list")
                    .WithDescription("List all loaded skills")
                    .WithAlias("ls")
                    .WithExample("skill", "list");
                skill.AddCommand<SkillInstallCommand>("install")
                    .WithDescription("Install a skill from URL, GitHub, or local path")
                    .WithAlias("i")
                    .WithExample("skill", "install", "github.com/user/repo/skills/l0_atomic/my_skill.md");
                skill.AddCommand<SkillExtractCommand>("extract")
                    .WithDescription("Auto-extract skills from successful conversation patterns")
                    .WithAlias("x")
                    .WithExample("skill", "extract");
                skill.AddCommand<SkillStatsCommand>("stats")
                    .WithDescription("Show skill statistics")
                    .WithAlias("s")
                    .WithExample("skill", "stats");
            });

            config.AddCommand<CompatCommand>("compat")
                .WithDescription("Agent Framework API compatibility gate")
                .WithAlias("c")
                .WithExample("compat");
        });

        return await app.RunAsync(args).ConfigureAwait(false);
    }

    private static async Task<int> RunSetupAsync()
    {
        var wizard = new InteractiveSetupWizard(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        await wizard.RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static void ScanEntryPoints()
    {
        var entryTypes = new List<Type>();

        ScanLoadedAssemblies(entryTypes);
        LoadPluginAssemblies(entryTypes);

        foreach (var type in entryTypes)
        {
            try
            {
                if (Activator.CreateInstance(type, type.IsPublic) is ILTAIEntryPoint entry)
                {
                    foreach (var candidate in new[] { "host", "serve", "mcp", "tui", "webapp" })
                    {
                        try
                        {
                            if (entry.CanHandle(candidate))
                                _entryPoints.TryAdd(candidate, entry);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }
    }

    private static void ScanLoadedAssemblies(List<Type> entryTypes)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic) continue;
            try
            {
                foreach (var type in assembly.GetTypes())
                {
                    if (typeof(ILTAIEntryPoint).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract)
                        entryTypes.Add(type);
                }
            }
            catch { }
        }
    }

    private static void LoadPluginAssemblies(List<Type> entryTypes)
    {
        var pluginsDir = Path.Combine(AppContext.BaseDirectory, "plugins");
        if (!Directory.Exists(pluginsDir)) return;

        foreach (var dll in Directory.GetFiles(pluginsDir, "*.dll"))
        {
            try
            {
                var assembly = Assembly.LoadFrom(dll);
                foreach (var type in assembly.GetTypes())
                {
                    if (typeof(ILTAIEntryPoint).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract)
                        entryTypes.Add(type);
                }
            }
            catch { }
        }
    }
}
