using System.Reflection;
using System.Text.Json;
using Spectre.Console;
using LTAI.Core.Interfaces;
using LTAI.Knowledge.Core;
using LTAI.Core.Messaging;

namespace LTAI.Cli;

partial class Program
{
    private static readonly Dictionary<string, ILTAIEntryPoint> _entryPoints = new();

    public static async Task<int> Main(string[] args)
    {
        ScanEntryPoints();

        if (args.Length > 0)
        {
            var cmd = args[0].ToLowerInvariant();

            if (cmd is "add")             { await RunAddAsync(args[1..]); return 0; }
            if (cmd is "remove" or "rm")  { await RunRemoveAsync(args[1..]); return 0; }
            if (cmd is "up" or "start")   { await RunUpAsync(args[1..]); return 0; }
            if (cmd is "down" or "stop")  { await RunDownAsync(args[1..]); return 0; }
            if (cmd is "ps" or "status")  { await RunPsAsync(); return 0; }
            if (cmd is "update")          { await RunUpdateAsync(args[1..]); return 0; }
            if (cmd is "env")             { await RunEnvAsync(args[1..]); return 0; }
            if (cmd is "git")             { await RunGitAsync(args[1..]); return 0; }
            if (cmd is "dev")             { await RunDevAsync(args[1..]); return 0; }
            if (cmd is "debug" or "test") { await RunDebugAsync(args[1..]); return 0; }

            if (_entryPoints.TryGetValue(cmd, out var entry))
            {
                await entry.RunAsync(args[1..]);
                return 0;
            }
        }

        PrintBanner();
        return 0;
    }

    // ════════════════════════════════════════════════════════════════
    // Shared helpers
    // ════════════════════════════════════════════════════════════════

    private static void PrintBanner()
    {
        AnsiConsole.Write(new FigletText("LTAI OS").Color(Color.Cyan1));
        AnsiConsole.MarkupLine("[bold cyan]V1.0 — Agent OS Bootstrapper[/]\n");
        AnsiConsole.MarkupLine("[bold]Quick Start:[/]\n  ltai up            Start TUI (default)\n");
        AnsiConsole.MarkupLine("[bold]Commands:[/]\n  setup, add, remove, up, down, ps, update, env, git, dev, model");
    }

    private static void PrintFileList(string label, JsonElement root, string property, Color color)
    {
        if (!root.TryGetProperty(property, out var arr) || arr.GetArrayLength() == 0) return;
        AnsiConsole.MarkupLine($"  [bold]{label}:[/]");
        foreach (var f in arr.EnumerateArray())
            AnsiConsole.MarkupLine($"    [{color}]{f.GetString()}[/]");
    }

    private static string? ResolveEnvValue(string key)
    {
        var config = CliConfig.Load();
        var val = key switch
        {
            "LTAI_HOME" => config.InstallPath,
            "LTAI_WORKSPACE" => config.WorkspaceRoot,
            "LTAI_L1_API_KEY" => config.L1ApiKey,
            "LTAI_L2_API_KEY" => config.L2ApiKey,
            _ => null
        };
        if (!string.IsNullOrWhiteSpace(val)) return val;
        return Environment.GetEnvironmentVariable(key);
    }

    internal static bool IsSecretKey(string key) =>
        key.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase) ||
        key.EndsWith("_SECRET", StringComparison.OrdinalIgnoreCase);

    internal static string MaskSecret(string value)
    {
        if (value.Length <= 8) return "****";
        return $"{value[..4]}****{value[^4..]}";
    }

    internal static void AddEnvRow(Table table, string name, string? value, string source, bool isSecret)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            table.AddRow(new Markup($"[dim]{name}[/]"), new Markup("[grey](not set)[/]"), new Markup("[dim]—[/]"));
            return;
        }
        table.AddRow(new Markup($"[bold]{name}[/]"), new Markup(isSecret ? MaskSecret(value) : value), new Markup($"[dim]{source}[/]"));
    }

    internal static string? FindSolutionFile()
    {
        return Directory.GetFiles(Directory.GetCurrentDirectory(), "*.sln").FirstOrDefault();
    }

    private static void ScanEntryPoints()
    {
        try { _ = typeof(LTAI.TUI.TuiEntryPointRegistration); } catch { }

        var entryTypes = new List<Type>();
        ScanLoadedAssemblies(entryTypes);
        LoadPluginAssemblies(entryTypes);

        foreach (var mode in LTAIEntryPointRegistry.RegisteredModes)
        {
            var entry = LTAIEntryPointRegistry.Get(mode);
            if (entry != null) _entryPoints.TryAdd(mode, entry);
        }

        foreach (var type in entryTypes)
        {
            try
            {
                if (Activator.CreateInstance(type, nonPublic: true) is ILTAIEntryPoint entry)
                {
                    foreach (var candidate in new[] { "host", "serve", "mcp", "tui", "webapp", "core", "webapi", "desktop" })
                    {
                        try { if (entry.CanHandle(candidate)) _entryPoints.TryAdd(candidate, entry); }
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
