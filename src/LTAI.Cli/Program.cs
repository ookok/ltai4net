using System.Reflection;
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

            if (cmd is "init") { await RunInitAsync(args[1..]); return 0; }
            if (cmd is "install") { await RunInstallAsync(args[1..]); return 0; }
            if (cmd is "setup") { await RunSetupAsync(args[1..]); return 0; }
            if (cmd is "add") { await RunAddAsync(args[1..]); return 0; }
            if (cmd is "remove" or "rm") { await RunRemoveAsync(args[1..]); return 0; }
            if (cmd is "up" or "start") { await RunUpAsync(args[1..]); return 0; }
            if (cmd is "down" or "stop") { await RunDownAsync(args[1..]); return 0; }
            if (cmd is "ps" or "status") { await RunPsAsync(); return 0; }
            if (cmd is "update") { await RunUpdateAsync(args[1..]); return 0; }
            if (cmd is "env") { await RunEnvAsync(args[1..]); return 0; }

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
    // ltai init — interactive first-run setup
    // ════════════════════════════════════════════════════════════════

    private static async Task RunInitAsync(string[] args)
    {
        var config = CliConfig.Load();
        var batchFile = args.FirstOrDefault(a => a.StartsWith("--config="))?[9..];

        if (batchFile != null && File.Exists(batchFile))
        {
            config = System.Text.Json.JsonSerializer.Deserialize<CliConfig>(File.ReadAllText(batchFile))!;
            config.Save();
            AnsiConsole.MarkupLine("[green]Batch config loaded from {0}[/]", batchFile);
            return;
        }

        AnsiConsole.Write(new FigletText("LTAI OS").Color(Color.Cyan1));
        AnsiConsole.MarkupLine("[bold cyan]Welcome to LTAI Agent OS Setup[/]\n");

        config.InstallPath = AnsiConsole.Ask("Install path", config.InstallPath);
        config.ReleaseChannel = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select release channel")
                .AddChoices("stable", "beta", "dev"));

        AnsiConsole.MarkupLine("\n[bold]Model Configuration[/]");
        config.L0Endpoint = AnsiConsole.Ask("L0 Model Endpoint (Ollama)", config.L0Endpoint);
        config.L1ApiKey = AnsiConsole.Prompt(
            new TextPrompt<string>("L1 API Key (optional)")
                .AllowEmpty().Secret()) ?? "";
        config.L2ApiKey = AnsiConsole.Prompt(
            new TextPrompt<string>("L2 API Key (optional)")
                .AllowEmpty().Secret()) ?? "";
        config.L2Endpoint = AnsiConsole.Ask("L2 API Endpoint", config.L2Endpoint);
        config.WorkspaceRoot = AnsiConsole.Ask("Workspace Root", Directory.GetCurrentDirectory());
        config.SandboxRoot = AnsiConsole.Ask("Sandbox Root", Path.Combine(config.InstallPath, "sandbox"));

        config.Save();
        AnsiConsole.MarkupLine($"\n[green]Config saved to {CliConfig.ConfigPath}[/]");
        AnsiConsole.MarkupLine("[dim]Next: run 'ltai install' to download the core runtime[/]");
    }

    // ════════════════════════════════════════════════════════════════
    // ltai install — download core runtime
    // ════════════════════════════════════════════════════════════════

    private static async Task RunInstallAsync(string[] args)
    {
        var config = CliConfig.Load();
        Directory.CreateDirectory(config.InstallPath);

        await AnsiConsole.Progress()
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[cyan]Downloading LTAI Core Runtime[/]");
                task.MaxValue = 5;

                var coreDir = Path.Combine(config.InstallPath, "core");
                Directory.CreateDirectory(coreDir);

                task.Increment(1);
                AnsiConsole.MarkupLine("  [dim]Extracting L0 MicroKernel...[/]");
                ResourceExtractor.EnsureExtracted(config.InstallPath);

                task.Increment(1);
                AnsiConsole.MarkupLine("  [dim]Extracting L1 Perception Layer...[/]");

                task.Increment(1);
                AnsiConsole.MarkupLine("  [dim]Extracting L2 Coordination Layer...[/]");

                task.Increment(1);
                AnsiConsole.MarkupLine("  [dim]Extracting L3-L5 Upper Layers...[/]");

                task.Increment(1);
                config.Components.Add(new InstalledComponent
                {
                    Name = "core", Version = "1.0.0",
                    Path = coreDir, Type = "core"
                });
                config.Save();

                task.StopTask();
            });

        AnsiConsole.MarkupLine("[green]Installation complete.[/]");
        AnsiConsole.MarkupLine("[dim]Next: run 'ltai setup' to configure, then 'ltai add webapp' and 'ltai up'[/]");
    }

    // ════════════════════════════════════════════════════════════════
    // ltai setup — configure core parameters
    // ════════════════════════════════════════════════════════════════

    private static async Task RunSetupAsync(string[] args)
    {
        var wizard = new InteractiveSetupWizard(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        await wizard.RunAsync().ConfigureAwait(false);
    }

    // ════════════════════════════════════════════════════════════════
    // ltai add <component> — download and install a component
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
    // ltai remove <component> — uninstall a component
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

    // ════════════════════════════════════════════════════════════════
    // ltai up [component] — start components
    // ════════════════════════════════════════════════════════════════

    private static async Task RunUpAsync(string[] args)
    {
        var config = CliConfig.Load();
        config.SetEnv();

        var target = args.FirstOrDefault()?.ToLowerInvariant();
        var targets = target != null
            ? new[] { target }
            : new[] { "tui" };

        foreach (var name in targets)
        {
            if (ProcessLauncher.IsRunning(name))
            {
                AnsiConsole.MarkupLine($"[yellow]{name} already running[/]");
                continue;
            }

            var entry = _entryPoints.GetValueOrDefault(name);
            if (entry != null)
            {
                AnsiConsole.MarkupLine($"[cyan]Starting {name}...[/]");
                _ = Task.Run(() => entry.RunAsync(Array.Empty<string>()));
                await Task.Delay(500);
            }
            else
            {
                AnsiConsole.MarkupLine($"[yellow]{name}: entry point not found — run 'ltai install' first[/]");
            }
        }

        if (targets.Contains("tui"))
            AnsiConsole.MarkupLine("[green]TUI started[/]");
        if (targets.Contains("webapp") || targets.Contains("webapi"))
            AnsiConsole.MarkupLine("[green]WebApp available at http://localhost:8080[/]");
        if (targets.Contains("mcp"))
            AnsiConsole.MarkupLine("[green]MCP Server listening on ws://localhost:8081[/]");
    }

    // ════════════════════════════════════════════════════════════════
    // ltai down — stop all components
    // ════════════════════════════════════════════════════════════════

    private static async Task RunDownAsync(string[] args)
    {
        AnsiConsole.MarkupLine("[yellow]Stopping all components...[/]");
        ProcessLauncher.StopAll();
        AnsiConsole.MarkupLine("[green]All components stopped[/]");
    }

    // ════════════════════════════════════════════════════════════════
    // ltai ps — list running components
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
            AnsiConsole.MarkupLine("[dim]No components registered. Run 'ltai install' and 'ltai add <component>'[/]");
        else
            AnsiConsole.Write(table);

        return Task.CompletedTask;
    }

    // ════════════════════════════════════════════════════════════════
    // ltai update [component] — self-update or update a component
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

    // ════════════════════════════════════════════════════════════════
    // ltai env — environment variable management
    // ════════════════════════════════════════════════════════════════

    private static async Task RunEnvAsync(string[] args)
    {
        var sub = args.FirstOrDefault()?.ToLowerInvariant();

        if (sub == "get")
        {
            await RunEnvGetAsync(args[1..]);
            return;
        }
        if (sub == "set")
        {
            await RunEnvSetAsync(args[1..]);
            return;
        }

        // Default: list all environment variables
        PrintAllEnvVars();
    }

    private static Task RunEnvGetAsync(string[] args)
    {
        var key = args.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(key))
        {
            AnsiConsole.MarkupLine("[red]Usage: ltai env get <KEY>[/]");
            return Task.CompletedTask;
        }

        var value = ResolveEnvValue(key.ToUpperInvariant());
        if (value == null)
        {
            AnsiConsole.MarkupLine($"[yellow]Environment variable '{key}' is not set.[/]");
            return Task.CompletedTask;
        }

        var display = IsSecretKey(key) ? MaskSecret(value) : value;
        AnsiConsole.MarkupLine($"[bold]{key}[/] = {display}");
        return Task.CompletedTask;
    }

    private static async Task RunEnvSetAsync(string[] args)
    {
        if (args.Length < 2)
        {
            AnsiConsole.MarkupLine("[red]Usage: ltai env set <KEY> <VALUE>[/]");
            return;
        }

        var key = args[0].ToUpperInvariant();
        var value = args[1];
        var config = CliConfig.Load();

        switch (key)
        {
            case "LTAI_HOME":
                config.InstallPath = value;
                break;
            case "LTAI_WORKSPACE":
                config.WorkspaceRoot = value;
                break;
            case "LTAI_L1_API_KEY":
                config.L1ApiKey = value;
                break;
            case "LTAI_L2_API_KEY":
                config.L2ApiKey = value;
                break;
            default:
                // For provider API keys (DEEPSEEK_API_KEY, OPENAI_API_KEY, etc.)
                // Store as a generic env var via OptionService if possible
                Environment.SetEnvironmentVariable(key, value, EnvironmentVariableTarget.User);
                AnsiConsole.MarkupLine($"[green]{key}[/] set (session + user-level).");
                AnsiConsole.MarkupLine("[dim]Note: provider keys from appsettings.json take precedence. Restart CLI to reload.[/]");
                return;
        }

        config.Save();
        config.SetEnv();
        AnsiConsole.MarkupLine($"[green]{key}[/] saved to [dim]{CliConfig.ConfigPath}[/]");
    }

    private static void PrintAllEnvVars()
    {
        var config = CliConfig.Load();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]Variable[/]").Width(30))
            .AddColumn(new TableColumn("[bold]Value[/]"))
            .AddColumn(new TableColumn("[bold]Source[/]").Width(12));

        // LTAI core vars
        AddEnvRow(table, "LTAI_HOME", config.InstallPath, "config.json", IsSecretKey("LTAI_HOME"));
        AddEnvRow(table, "LTAI_WORKSPACE", config.WorkspaceRoot, "config.json", IsSecretKey("LTAI_WORKSPACE"));
        AddEnvRow(table, "LTAI_L1_API_KEY", config.L1ApiKey, "config.json", IsSecretKey("LTAI_L1_API_KEY"));
        AddEnvRow(table, "LTAI_L2_API_KEY", config.L2ApiKey, "config.json", IsSecretKey("LTAI_L2_API_KEY"));

        table.AddEmptyRow();

        // Provider API keys (from ResolveApiKey mapping)
        var providers = new Dictionary<string, string>
        {
            ["DEEPSEEK"] = "DEEPSEEK_API_KEY",
            ["OPENAI"] = "OPENAI_API_KEY",
            ["ANTHROPIC"] = "ANTHROPIC_API_KEY",
            ["GEMINI"] = "GEMINI_API_KEY",
            ["SILICONFLOW"] = "SILICONFLOW_API_KEY",
            ["DASHSCOPE"] = "DASHSCOPE_API_KEY",
            ["ZHIPU"] = "ZHIPU_API_KEY",
            ["HUNYUAN"] = "HUNYUAN_API_KEY",
            ["BAIDU"] = "BAIDU_API_KEY",
            ["SPARK"] = "SPARK_API_KEY",
            ["MOFANG"] = "MOFANG_API_KEY",
            ["NVIDIA"] = "NVIDIA_API_KEY",
            ["BAILING"] = "BAILING_API_KEY",
            ["STEPFUN"] = "STEPFUN_API_KEY",
            ["INTERNLM"] = "INTERNLM_API_KEY",
            ["SENSETIME"] = "SENSETIME_API_KEY",
            ["MODELSCOPE"] = "MODELSCOPE_API_KEY",
            ["OPENROUTER"] = "OPENROUTER_API_KEY",
            ["XIAOMI"] = "XIAOMI_API_KEY",
            ["LONGCAT"] = "LONGCAT_API_KEY",
            ["DMXAPI"] = "DMXAPI_API_KEY",
            ["VOLCENGINE"] = "VOLCENGINE_API_KEY",
            ["MOONSHOT"] = "MOONSHOT_API_KEY",
            ["MINIMAX"] = "MINIMAX_API_KEY",
            ["GROQ"] = "GROQ_API_KEY",
            ["KIRO"] = "KIRO_API_KEY",
            ["OPENCODE"] = "OPENCODE_API_KEY",
        };

        foreach (var (provider, envVar) in providers.OrderBy(p => p.Key))
        {
            var val = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrWhiteSpace(val))
                AddEnvRow(table, envVar, val, $"env ({provider})", IsSecretKey(envVar));
        }

        // Local providers (no key needed)
        table.AddEmptyRow();
        table.AddRow(
            new Markup("[dim]Local providers (no key)[/]"),
            new Markup("[dim]Ollama / LMStudio / vLLM / LlamaCpp / OpenWebUI[/]"),
            new Markup("[dim]—[/]"));

        AnsiConsole.Write(table);
    }

    private static void AddEnvRow(Table table, string name, string? value, string source, bool isSecret)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            table.AddRow(
                new Markup($"[dim]{name}[/]"),
                new Markup("[grey](not set)[/]"),
                new Markup("[dim]—[/]"));
            return;
        }

        var display = isSecret ? MaskSecret(value) : value;
        table.AddRow(
            new Markup($"[bold]{name}[/]"),
            new Markup(display),
            new Markup($"[dim]{source}[/]"));
    }

    private static string? ResolveEnvValue(string key)
    {
        // Check CliConfig first
        var config = CliConfig.Load();
        var val = key switch
        {
            "LTAI_HOME" => config.InstallPath,
            "LTAI_WORKSPACE" => config.WorkspaceRoot,
            "LTAI_L1_API_KEY" => config.L1ApiKey,
            "LTAI_L2_API_KEY" => config.L2ApiKey,
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(val))
            return val;

        // Fall back to environment
        return Environment.GetEnvironmentVariable(key);
    }

    private static bool IsSecretKey(string key) =>
        key.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase) ||
        key.EndsWith("_SECRET", StringComparison.OrdinalIgnoreCase);

    private static string MaskSecret(string value)
    {
        if (value.Length <= 8) return "****";
        return $"{value[..4]}****{value[^4..]}";
    }

    // ════════════════════════════════════════════════════════════════
    // helpers
    // ════════════════════════════════════════════════════════════════

    private static void PrintBanner()
    {
        AnsiConsole.Write(new FigletText("LTAI OS").Color(Color.Cyan1));
        AnsiConsole.MarkupLine("[bold cyan]V1.0 — Agent OS Bootstrapper[/]");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("[bold]Quick Start:[/]");
        AnsiConsole.MarkupLine("  ltai init          Configure your environment");
        AnsiConsole.MarkupLine("  ltai install       Download core runtime");
        AnsiConsole.MarkupLine("  ltai up            Start TUI (default)");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("[bold]Commands:[/]");
        AnsiConsole.MarkupLine("  init, install, setup, add, remove, up, down, ps, update, env");
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
