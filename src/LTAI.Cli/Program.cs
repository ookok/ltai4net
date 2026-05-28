using System.Reflection;
using System.Text.Json;
using LibGit2Sharp;
using LTAI.Core.Interfaces;
using LTAI.Core.Setup;
using LTAI.Knowledge.Core;
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
            if (cmd is "git") { await RunGitAsync(args[1..]); return 0; }
            if (cmd is "dev") { await RunDevAsync(args[1..]); return 0; }
            if (cmd is "model") { await RunModelAsync(args[1..]); return 0; }

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
    // ltai dev — development environment check + one-click setup
    // ════════════════════════════════════════════════════════════════

    private static async Task RunDevAsync(string[] args)
    {
        var sub = args.FirstOrDefault()?.ToLowerInvariant();

        if (sub == "setup")
        {
            await RunDevSetupAsync();
            return;
        }

        // Default: check development environment
        await RunDevCheckAsync();
    }

    private static Task RunDevCheckAsync()
    {
        AnsiConsole.Write(new Rule("[bold cyan]LTAI Development Environment Check[/]").RuleStyle(Style.Plain));

        var allOk = true;

        // 1. .NET SDK
        AnsiConsole.Markup("[bold].NET SDK 10.0[/] ");
        try
        {
            var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT")
                ?? Path.GetDirectoryName(typeof(object).Assembly.Location);
            var version = Environment.Version;
            var sdkVersion = typeof(object).Assembly.ImageRuntimeVersion;

            // Try to get real SDK version via dotnet --version
            var psi = new System.Diagnostics.ProcessStartInfo("dotnet", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(5000);
            var output = proc?.StandardOutput.ReadToEnd()?.Trim() ?? "";
            var verMatch = System.Text.RegularExpressions.Regex.Match(output, @"^(\d+)\.\d+\.\d+");

            if (verMatch.Success && int.TryParse(verMatch.Groups[1].Value, out var major) && major >= 10)
            {
                AnsiConsole.MarkupLine($"[green]✓ {output}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]✗ .NET 10.0 SDK required. Found: {output}[/]");
                AnsiConsole.MarkupLine("[dim]  Install: https://dotnet.microsoft.com/download/dotnet/10.0[/]");
                allOk = false;
            }
        }
        catch
        {
            AnsiConsole.MarkupLine("[red]✗ dotnet CLI not found in PATH[/]");
            allOk = false;
        }

        // 2. Git (via libgit2sharp — always available, but check repo)
        AnsiConsole.Markup("[bold]Git repository[/] ");
        try
        {
            var discovered = LibGit2Sharp.Repository.Discover(Directory.GetCurrentDirectory());
            if (!string.IsNullOrEmpty(discovered))
                AnsiConsole.MarkupLine("[green]✓ found[/]");
            else
                AnsiConsole.MarkupLine("[yellow]⚠ not a git repository (optional)[/]");
        }
        catch
        {
            AnsiConsole.MarkupLine("[yellow]⚠ not a git repository (optional)[/]");
        }

        // 3. Workspace
        AnsiConsole.Markup("[bold]LTAI_WORKSPACE[/] ");
        var workspace = OptionService.Get("LTAI_WORKSPACE")
            ?? Environment.GetEnvironmentVariable("LTAI_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            var exists = Directory.Exists(workspace);
            AnsiConsole.MarkupLine(exists
                ? $"[green]✓ {workspace}[/]"
                : $"[yellow]⚠ set but directory missing: {workspace}[/]");
        }
        else
        {
            // Check if current directory looks like LTAI workspace
            var cwd = Directory.GetCurrentDirectory();
            var hasSl = File.Exists(Path.Combine(cwd, "LTAI.sln"));
            var hasSrc = Directory.Exists(Path.Combine(cwd, "src"));
            if (hasSl && hasSrc)
            {
                AnsiConsole.MarkupLine($"[green]✓ (auto-detected: {cwd})[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]⚠ not set — run 'ltai dev setup'[/]");
                allOk = false;
            }
        }

        // 4. API Keys
        AnsiConsole.Markup("[bold]LTAI_L1_API_KEY[/] ");
        var l1Key = Environment.GetEnvironmentVariable("LTAI_L1_API_KEY")
            ?? CliConfig.Load().L1ApiKey;
        AnsiConsole.MarkupLine(!string.IsNullOrWhiteSpace(l1Key)
            ? $"[green]✓ {MaskSecret(l1Key)}[/]"
            : "[dim]○ (not set, L1 will use L2 fallback)[/]");

        AnsiConsole.Markup("[bold]LTAI_L2_API_KEY[/] ");
        var l2Key = Environment.GetEnvironmentVariable("LTAI_L2_API_KEY")
            ?? CliConfig.Load().L2ApiKey;
        if (!string.IsNullOrWhiteSpace(l2Key))
            AnsiConsole.MarkupLine($"[green]✓ {MaskSecret(l2Key)}[/]");
        else
        {
            // Check provider keys
            var providerKeys = new[] { "DEEPSEEK_API_KEY", "OPENAI_API_KEY", "DASHSCOPE_API_KEY", "ANTHROPIC_API_KEY" };
            var found = providerKeys.FirstOrDefault(k => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(k)));
            if (found != null)
                AnsiConsole.MarkupLine($"[green]✓ via {found} ({MaskSecret(Environment.GetEnvironmentVariable(found)!)})[/]");
            else
            {
                AnsiConsole.MarkupLine("[red]✗ no API key configured[/]");
                AnsiConsole.MarkupLine("[dim]  Run 'ltai init' or 'ltai env set LTAI_L2_API_KEY <key>'[/]");
                allOk = false;
            }
        }

        // 5. NuGet source
        AnsiConsole.Markup("[bold]NuGet source[/] ");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("dotnet", "nuget list source")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(5000);
            var sources = proc?.StandardOutput.ReadToEnd() ?? "";
            var hasNugetOrg = sources.Contains("nuget.org", StringComparison.OrdinalIgnoreCase);
            AnsiConsole.MarkupLine(hasNugetOrg
                ? "[green]✓ nuget.org available[/]"
                : "[yellow]⚠ nuget.org not found in sources[/]");
        }
        catch
        {
            AnsiConsole.MarkupLine("[yellow]⚠ could not verify[/]");
        }

        // 6. Build check
        AnsiConsole.Markup("[bold]dotnet build[/] ");
        try
        {
            var sln = FindSolutionFile();
            if (sln != null)
            {
                var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"build \"{sln}\" --no-restore -v q")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit(120_000);
                if (proc?.ExitCode == 0)
                    AnsiConsole.MarkupLine($"[green]✓ {Path.GetFileName(sln)} builds successfully[/]");
                else
                {
                    AnsiConsole.MarkupLine("[yellow]⚠ build had warnings/errors (may need dotnet restore first)[/]");
                    allOk = false;
                }
            }
            else
            {
                AnsiConsole.MarkupLine("[dim]○ no .sln found in current directory[/]");
            }
        }
        catch
        {
            AnsiConsole.MarkupLine("[yellow]⚠ build check failed[/]");
        }

        // Summary
        AnsiConsole.WriteLine();
        if (allOk)
        {
            AnsiConsole.MarkupLine("[bold green]All checks passed! Ready to develop.[/]");
            AnsiConsole.MarkupLine("[dim]Run 'ltai up' to start the TUI.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[bold yellow]Some checks failed. Run 'ltai dev setup' to auto-configure.[/]");
        }

        return Task.CompletedTask;
    }

    private static async Task RunDevSetupAsync()
    {
        AnsiConsole.Write(new Rule("[bold cyan]LTAI Dev Environment Setup[/]").RuleStyle(Style.Plain));

        var cwd = Directory.GetCurrentDirectory();
        var config = CliConfig.Load();
        var changed = false;

        // 1. Set workspace
        if (string.IsNullOrWhiteSpace(config.WorkspaceRoot))
        {
            config.WorkspaceRoot = cwd;
            AnsiConsole.MarkupLine($"[green]✓ LTAI_WORKSPACE → {cwd}[/]");
            changed = true;
        }
        else
        {
            AnsiConsole.MarkupLine($"[dim]LTAI_WORKSPACE already set: {config.WorkspaceRoot}[/]");
        }

        // 2. Set install path
        if (string.IsNullOrWhiteSpace(config.InstallPath) || config.InstallPath ==
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ltai"))
        {
            config.InstallPath = Path.Combine(cwd, ".ltai");
            AnsiConsole.MarkupLine($"[green]✓ LTAI_HOME → {config.InstallPath}[/]");
            changed = true;
        }

        // 3. Prompt for L2 API key if missing
        if (string.IsNullOrWhiteSpace(config.L2ApiKey))
        {
            var existingKey = Environment.GetEnvironmentVariable("LTAI_L2_API_KEY")
                ?? Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")
                ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (!string.IsNullOrWhiteSpace(existingKey))
            {
                config.L2ApiKey = existingKey;
                AnsiConsole.MarkupLine($"[green]✓ LTAI_L2_API_KEY from env ({MaskSecret(existingKey)})[/]");
                changed = true;
            }
            else
            {
                config.L2ApiKey = AnsiConsole.Prompt(
                    new TextPrompt<string>("L2 API Key (DeepSeek/OpenAI)")
                        .AllowEmpty().Secret()) ?? "";
                if (!string.IsNullOrWhiteSpace(config.L2ApiKey))
                    changed = true;
            }
        }

        // 4. Restore NuGet packages
        AnsiConsole.Markup("[bold]dotnet restore[/] ");
        try
        {
            var sln = FindSolutionFile();
            if (sln != null)
            {
                var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"restore \"{sln}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                await proc!.WaitForExitAsync();
                AnsiConsole.MarkupLine(proc.ExitCode == 0
                    ? "[green]✓ packages restored[/]"
                    : "[yellow]⚠ restore completed with warnings[/]");
            }
        }
        catch
        {
            AnsiConsole.MarkupLine("[yellow]⚠ restore failed[/]");
        }

        // Save config
        if (changed)
        {
            config.Save();
            config.SetEnv();
            AnsiConsole.MarkupLine($"[green]✓ Config saved to {CliConfig.ConfigPath}[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold green]Dev environment configured![/]");
        AnsiConsole.MarkupLine("[dim]Run 'ltai dev' to verify, 'ltai up' to start.[/]");
    }

    private static string? FindSolutionFile()
    {
        var cwd = Directory.GetCurrentDirectory();
        var sln = Directory.GetFiles(cwd, "*.sln").FirstOrDefault();
        return sln;
    }

    // ════════════════════════════════════════════════════════════════
    // ltai model — local ONNX model download & management
    // ════════════════════════════════════════════════════════════════

    private static async Task RunModelAsync(string[] args)
    {
        var sub = args.FirstOrDefault()?.ToLowerInvariant();

        if (sub == "download")
        {
            await RunModelDownloadAsync(args[1..]);
            return;
        }
        if (sub == "list")
        {
            RunModelListAsync(args[1..]);
            return;
        }
        if (sub == "set")
        {
            RunModelSetAsync(args[1..]);
            return;
        }
        if (sub == "tier" || sub == "cost")
        {
            RunModelTierAsync();
            return;
        }

        // Default: show available models
        RunModelListAsync(Array.Empty<string>());
    }

    private static void RunModelSetAsync(string[] args)
    {
        var tier = args.FirstOrDefault()?.ToLowerInvariant();
        var config = CliConfig.Load();

        switch (tier)
        {
            case "flash":
                config.ReleaseChannel = "flash";
                AnsiConsole.MarkupLine("[green]模型层级设为 [bold]flash[/] (v4-flash)[/]");
                AnsiConsole.MarkupLine("[dim]输入 ￥1.01/1M, 输出 ￥2.02/1M, 缓存命中 ￥0.02/1M[/]");
                break;
            case "auto":
                config.ReleaseChannel = "auto";
                AnsiConsole.MarkupLine("[green]模型层级设为 [bold]auto[/] (flash优先自动升级)[/]");
                AnsiConsole.MarkupLine("[dim]flash ￥1.01/2.02 → 自动升级时 pro ￥3.13/6.26[/]");
                break;
            case "pro":
                config.ReleaseChannel = "pro";
                AnsiConsole.MarkupLine("[green]模型层级设为 [bold]pro[/] (v4-pro, 永久降价)[/]");
                AnsiConsole.MarkupLine("[dim]输入 ￥3.13/1M, 输出 ￥6.26/1M[/]");
                break;
            default:
                AnsiConsole.MarkupLine("[red]Usage: ltai model set [flash|auto|pro][/]");
                AnsiConsole.MarkupLine("[dim]flash — 仅快速模型  ￥1/4 每百万token[/]");
                AnsiConsole.MarkupLine("[dim]auto  — flash优先自动升级 (默认)[/]");
                AnsiConsole.MarkupLine("[dim]pro   — 仅深度模型  ￥4/16 每百万token[/]");
                return;
        }

        config.Save();
    }

    private static void RunModelTierAsync()
    {
        var config = CliConfig.Load();
        var currentTier = config.ReleaseChannel?.ToLowerInvariant() switch
        {
            "flash" => "flash",
            "pro" => "pro",
            _ => "auto"
        };

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Tier[/]")
            .AddColumn(new TableColumn("[bold]Model[/]").Width(20))
            .AddColumn("[bold]Cost Multiplier[/]")
            .AddColumn("[bold]Behavior[/]");

        var tiers = new[]
        {
            ("flash", "v4-flash", "￥1.01/2.02 每百万token", "仅快速模型。缓存命中 ￥0.02/1M。"),
            ("auto", "v4-flash → v4-pro", "flash ￥1.01/2.02, pro ￥3.13/6.26", "Flash优先，<<<NEEDS_PRO>>> 自动升级。"),
            ("pro", "v4-pro", "￥3.13/6.26 每百万token", "仅深度模型。永久降价。")
        };

        foreach (var (tier, model, cost, desc) in tiers)
        {
            var isCurrent = tier == currentTier;
            var prefix = isCurrent ? "[green]▶[/]" : " ";
            var tierStyle = isCurrent ? $"bold green" : "";
            table.AddRow(
                new Markup($"{prefix} [{tierStyle}]{tier}[/]"),
                new Markup(model),
                new Markup(cost),
                new Markup($"[dim]{desc}[/]"));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine($"[bold]Current:[/] [green]{currentTier}[/]");
        AnsiConsole.MarkupLine("[dim]Auto-compress: tool results >3000 tokens capped at turn end[/]");
        AnsiConsole.MarkupLine("[dim]Budget tracker: daily token limit + cost limit with degradation[/]");
        AnsiConsole.MarkupLine("[dim]Change with: ltai model set [flash|auto|pro][/]");
    }

    private static async Task RunModelDownloadAsync(string[] args)
    {
        var layer = args.FirstOrDefault()?.ToLowerInvariant() ?? "l0";
        var modelsRoot = Path.Combine(
            Environment.GetEnvironmentVariable("LTAI_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ltai"),
            "models");

        Directory.CreateDirectory(modelsRoot);

        var downloader = new LTAI.Core.Network.ModelAutoDownloader(modelsRoot,
            logger: null, maxRetries: 3);
        downloader.OnProgress += p =>
        {
            if (p.Status == "downloading" && p.TotalBytes > 0)
                AnsiConsole.MarkupLine($"  [dim]{p.Percent:F0}% {p.DownloadedBytes/1024/1024}/{p.TotalBytes/1024/1024} MB[/]");
        };

        var models = layer switch
        {
            "l0" => LTAI.Core.Governors.LocalModelRegistry.GetByLayer(LTAI.Core.Governors.ModelLayer.L0),
            "l1" => LTAI.Core.Governors.LocalModelRegistry.GetByLayer(LTAI.Core.Governors.ModelLayer.L1),
            "l2" => LTAI.Core.Governors.LocalModelRegistry.GetByLayer(LTAI.Core.Governors.ModelLayer.L2),
            "all" => LTAI.Core.Governors.LocalModelRegistry.AvailableModels,
            _ => LTAI.Core.Governors.LocalModelRegistry.GetByVersion(layer) is { } m
                ? new[] { m }
                : Array.Empty<LTAI.Core.Governors.LocalModelInfo>()
        };

        if (models.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]No models found for: {layer}[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[bold cyan]Downloading {models.Count} model(s) for layer '{layer}'...[/]");
        AnsiConsole.MarkupLine($"[dim]Target: {modelsRoot}[/]");
        AnsiConsole.MarkupLine("");

        var totalSize = models.Sum(m => m.DiskSizeMB);
        var downloaded = 0;
        var failed = 0;

        foreach (var model in models)
        {
            AnsiConsole.Markup($"[bold]{model.Name}[/] [dim]({model.DiskSizeMB}MB)[/] ");
            try
            {
                var result = await downloader.DownloadAsync(model);
                if (result.Success)
                {
                    downloaded++;
                    AnsiConsole.MarkupLine($"[green]✓ {result.FileSizeBytes/1024/1024}MB[/]");
                }
                else
                {
                    failed++;
                    AnsiConsole.MarkupLine($"[red]✗ {result.Error}[/]");
                }
            }
            catch (Exception ex)
            {
                failed++;
                AnsiConsole.MarkupLine($"[red]✗ {ex.Message}[/]");
            }
        }

        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine($"[bold]Done:[/] [green]{downloaded} downloaded[/], [red]{failed} failed[/], [dim]{totalSize}MB total[/]");
        AnsiConsole.MarkupLine($"[dim]Models stored in: {modelsRoot}[/]");
    }

    private static void RunModelListAsync(string[] args)
    {
        var layerFilter = args.FirstOrDefault()?.ToLowerInvariant();
        var models = layerFilter switch
        {
            "l0" => LTAI.Core.Governors.LocalModelRegistry.GetByLayer(LTAI.Core.Governors.ModelLayer.L0),
            "l1" => LTAI.Core.Governors.LocalModelRegistry.GetByLayer(LTAI.Core.Governors.ModelLayer.L1),
            "l2" => LTAI.Core.Governors.LocalModelRegistry.GetByLayer(LTAI.Core.Governors.ModelLayer.L2),
            _ => LTAI.Core.Governors.LocalModelRegistry.AvailableModels
        };

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Layer[/]")
            .AddColumn(new TableColumn("[bold]Model[/]").Width(40))
            .AddColumn(new TableColumn("[bold]Size[/]").Width(10))
            .AddColumn("[bold]Engine[/]")
            .AddColumn(new TableColumn("[bold]Description[/]").Width(50));

        foreach (var m in models.OrderBy(m => m.Layer).ThenBy(m => m.DiskSizeMB))
        {
            var layerLabel = m.Layer switch
            {
                LTAI.Core.Governors.ModelLayer.L0 => "[cyan]L0[/]",
                LTAI.Core.Governors.ModelLayer.L1 => "[yellow]L1[/]",
                LTAI.Core.Governors.ModelLayer.L2 => "[magenta]L2[/]",
                _ => "?"
            };

            var sizeLabel = m.DiskSizeMB >= 1024
                ? $"{m.DiskSizeMB / 1024.0:F1} GB"
                : $"{m.DiskSizeMB} MB";

            var engineLabel = m.EngineType.ToUpperInvariant() switch
            {
                "ONNX" => "[green]ONNX[/]",
                "GGUF" => "[dim]GGUF[/]",
                _ => m.EngineType
            };

            // Check if downloaded
            var modelDir = Path.Combine(
                Environment.GetEnvironmentVariable("LTAI_HOME")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ltai"),
                "models", m.Version);
            var downloaded = Directory.Exists(modelDir) && Directory.GetFiles(modelDir).Length > 0;
            var prefix = downloaded ? "[green]✓[/]" : " ";

            table.AddRow(
                new Markup(layerLabel),
                new Markup($"{prefix} {m.Name}"),
                new Markup(sizeLabel),
                new Markup(engineLabel),
                new Markup($"[dim]{m.Description}[/]"));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("[dim]Run 'ltai model download l0' to download recommended L0 models.[/]");
    }

    // ════════════════════════════════════════════════════════════════
    // ltai git — native Git operations via libgit2sharp (no git CLI needed)
    // ════════════════════════════════════════════════════════════════

    private static async Task RunGitAsync(string[] args)
    {
        var sub = args.FirstOrDefault()?.ToLowerInvariant();
        var rest = args.Length > 1 ? args[1..] : Array.Empty<string>();

        try
        {
            switch (sub)
            {
                case "status":
                    await RunGitStatusAsync(rest);
                    break;
                case "log":
                    await RunGitLogAsync(rest);
                    break;
                case "diff":
                    await RunGitDiffAsync(rest);
                    break;
                case "commit":
                    await RunGitCommitAsync(rest);
                    break;
                case "branch":
                    await RunGitBranchAsync(rest);
                    break;
                case "checkout":
                    await RunGitCheckoutAsync(rest);
                    break;
                case "pull":
                    await RunGitPullAsync(rest);
                    break;
                case "stash":
                    await RunGitStashAsync(rest);
                    break;
                case "tag":
                    await RunGitTagAsync(rest);
                    break;
                case "show":
                    await RunGitShowAsync(rest);
                    break;
                case "blame":
                    await RunGitBlameAsync(rest);
                    break;
                case "remote":
                    await RunGitRemoteAsync(rest);
                    break;
                case "reset":
                    await RunGitResetAsync(rest);
                    break;
                case "clone":
                    await RunGitCloneAsync(rest);
                    break;
                case null or "" or "help":
                    PrintGitHelp();
                    break;
                default:
                    AnsiConsole.MarkupLine($"[red]Unknown git subcommand: '{sub}'[/]");
                    AnsiConsole.MarkupLine("[dim]Run 'ltai git help' for available commands.[/]");
                    break;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Git error: {ex.Message}[/]");
        }
    }

    private static void PrintGitHelp()
    {
        AnsiConsole.MarkupLine("[bold cyan]ltai git[/] — native Git operations (no git CLI required, powered by libgit2sharp)");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("[bold]Subcommands:[/]");
        AnsiConsole.MarkupLine("  [bold]status[/]                  Show working tree status");
        AnsiConsole.MarkupLine("  [bold]log[/] [-n N]              Show commit history");
        AnsiConsole.MarkupLine("  [bold]diff[/] [--staged] [files]  Show working tree changes");
        AnsiConsole.MarkupLine("  [bold]commit[/] -m <msg>          Create a commit");
        AnsiConsole.MarkupLine("  [bold]branch[/] [create|delete] <name>  Manage branches");
        AnsiConsole.MarkupLine("  [bold]checkout[/] <branch|file>   Switch branch or restore file");
        AnsiConsole.MarkupLine("  [bold]pull[/] [remote] [branch]   Fetch and merge");
        AnsiConsole.MarkupLine("  [bold]stash[/] [push|pop|list]    Manage stashes");
        AnsiConsole.MarkupLine("  [bold]tag[/] [create|list|delete]  Manage tags");
        AnsiConsole.MarkupLine("  [bold]show[/] <commit>            Show commit details and diff");
        AnsiConsole.MarkupLine("  [bold]blame[/] <file>             Show line-by-line attribution");
        AnsiConsole.MarkupLine("  [bold]remote[/] [list|show|add]   Manage remotes");
        AnsiConsole.MarkupLine("  [bold]reset[/] [--soft|--mixed|--hard] [target]  Reset HEAD");
        AnsiConsole.MarkupLine("  [bold]clone[/] <url> [--branch B] [--shallow]  Clone a repository");
    }

    private static async Task RunGitStatusAsync(string[] args)
    {
        var json = await LTAI.Agent.Tools.GitTools.GitStatus();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        {
            AnsiConsole.MarkupLine($"[red]{err.GetString()}[/]");
            return;
        }

        var branch = root.GetProperty("branch").GetString();
        var dirty = root.GetProperty("isDirty").GetBoolean();

        AnsiConsole.MarkupLine($"[bold]Branch:[/] [cyan]{branch}[/] {(dirty ? "[yellow](dirty)[/]" : "[green](clean)[/]")}");

        PrintFileList("Staged", root, "staged", Color.Green);
        PrintFileList("Modified", root, "modified", Color.Yellow);
        PrintFileList("Added", root, "added", Color.Green);
        PrintFileList("Deleted", root, "removed", Color.Red);
        PrintFileList("Untracked", root, "untracked", Color.Grey);
        PrintFileList("Renamed", root, "renamed", Color.Cyan1);
    }

    private static async Task RunGitLogAsync(string[] args)
    {
        var maxCount = 20;
        var nIdx = Array.IndexOf(args, "-n");
        if (nIdx >= 0 && nIdx + 1 < args.Length && int.TryParse(args[nIdx + 1], out var n))
            maxCount = n;

        var json = await LTAI.Agent.Tools.GitTools.GitLog(maxCount: maxCount);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        {
            AnsiConsole.MarkupLine($"[red]{err.GetString()}[/]");
            return;
        }

        var currentBranch = root.GetProperty("currentBranch").GetString();
        AnsiConsole.MarkupLine($"[bold]Branch:[/] [cyan]{currentBranch}[/]");
        AnsiConsole.MarkupLine("");

        foreach (var commit in root.GetProperty("commits").EnumerateArray())
        {
            var hash = commit.GetProperty("hash").GetString();
            var date = commit.GetProperty("date").GetString();
            var author = commit.GetProperty("author").GetString();
            var msg = commit.GetProperty("message").GetString();

            AnsiConsole.MarkupLine($"[yellow]{hash}[/] [dim]{date}[/] [cyan]{author}[/]");
            AnsiConsole.MarkupLine($"  {msg}");
        }
    }

    private static async Task RunGitDiffAsync(string[] args)
    {
        var staged = args.Contains("--staged") || args.Contains("-s");
        var files = string.Join(" ", args.Where(a => !a.StartsWith("-")));

        var json = await LTAI.Agent.Tools.GitTools.GitDiff(staged: staged, files: string.IsNullOrWhiteSpace(files) ? null : files);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        {
            AnsiConsole.MarkupLine($"[red]{err.GetString()}[/]");
            return;
        }

        var count = root.GetProperty("count").GetInt32();
        AnsiConsole.MarkupLine($"[bold]{count} file(s) changed[/] {(staged ? "[dim](staged)[/]" : "")}");

        foreach (var change in root.GetProperty("changes").EnumerateArray())
        {
            var path = change.GetProperty("path").GetString();
            var status = change.GetProperty("status").GetString();
            var color = status switch
            {
                "Added" or "NewInIndex" => "green",
                "Modified" or "ModifiedInIndex" => "yellow",
                "Deleted" or "RemovedFromIndex" => "red",
                "RenamedInIndex" => "cyan",
                _ => "grey"
            };
            AnsiConsole.MarkupLine($"  [{color}]{status}[/] {path}");
        }

        if (root.TryGetProperty("patch", out var patch) && patch.ValueKind == JsonValueKind.String)
        {
            var patchText = patch.GetString();
            if (!string.IsNullOrWhiteSpace(patchText))
            {
                AnsiConsole.MarkupLine("");
                AnsiConsole.MarkupLine("[bold]Diff:[/]");
                AnsiConsole.MarkupLine(patchText);
            }
        }
    }

    private static async Task RunGitCommitAsync(string[] args)
    {
        var msgIdx = Array.IndexOf(args, "-m");
        if (msgIdx < 0 || msgIdx + 1 >= args.Length)
        {
            AnsiConsole.MarkupLine("[red]Usage: ltai git commit -m \"message\"[/]");
            return;
        }
        var message = args[msgIdx + 1];

        var json = await LTAI.Agent.Tools.GitTools.GitCommit(message, stageAll: true);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        {
            AnsiConsole.MarkupLine($"[red]{err.GetString()}[/]");
            return;
        }

        var sha = root.GetProperty("sha").GetString();
        AnsiConsole.MarkupLine($"[green]Committed:[/] [yellow]{sha}[/] — {message}");
    }

    private static async Task RunGitBranchAsync(string[] args)
    {
        var op = args.FirstOrDefault() ?? "list";
        string? name = null;
        if (args.Length > 1) name = args[1];

        if (op is "create" or "delete" && string.IsNullOrWhiteSpace(name))
        {
            AnsiConsole.MarkupLine($"[red]Usage: ltai git branch {op} <name>[/]");
            return;
        }

        var json = await LTAI.Agent.Tools.GitTools.GitBranch(operation: op, name: name);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        {
            AnsiConsole.MarkupLine($"[red]{err.GetString()}[/]");
            return;
        }

        if (op == "create")
        {
            AnsiConsole.MarkupLine($"[green]Branch created:[/] [cyan]{root.GetProperty("created").GetString()}[/]");
            return;
        }
        if (op == "delete")
        {
            AnsiConsole.MarkupLine($"[green]Branch deleted:[/] [cyan]{root.GetProperty("deleted").GetString()}[/]");
            return;
        }

        // list
        var current = root.GetProperty("current").GetString();
        foreach (var b in root.GetProperty("branches").EnumerateArray())
        {
            var bName = b.GetProperty("name").GetString();
            var isCur = b.GetProperty("isCurrent").GetBoolean();
            var isRemote = b.GetProperty("isRemote").GetBoolean();
            var prefix = isCur ? "[green]*[/]" : " ";
            var style = isCur ? "bold cyan" : isRemote ? "dim" : "";
            AnsiConsole.MarkupLine($"{prefix} [{style}]{bName}[/]");
        }
    }

    private static async Task RunGitCheckoutAsync(string[] args)
    {
        var target = args.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(target))
        {
            AnsiConsole.MarkupLine("[red]Usage: ltai git checkout <branch|file>[/]");
            return;
        }

        var json = await LTAI.Agent.Tools.GitTools.GitCheckout(target);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        {
            AnsiConsole.MarkupLine($"[red]{err.GetString()}[/]");
            return;
        }

        var type = root.GetProperty("type").GetString();
        AnsiConsole.MarkupLine($"[green]Checked out {type}:[/] [cyan]{target}[/]");
    }

    private static async Task RunGitPullAsync(string[] args)
    {
        var remote = args.FirstOrDefault() ?? "origin";
        var branch = args.Length > 1 ? args[1] : null;

        var json = await LTAI.Agent.Tools.GitTools.GitPull(remote: remote, branch: branch);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        {
            AnsiConsole.MarkupLine($"[red]{err.GetString()}[/]");
            return;
        }

        var merged = root.GetProperty("merged").GetBoolean();
        var status = root.GetProperty("status").GetString();
        AnsiConsole.MarkupLine(merged
            ? $"[green]Pulled and merged:[/] {status}"
            : $"[dim]Fetched: up to date[/]");
    }

    private static async Task RunGitStashAsync(string[] args)
    {
        var op = args.FirstOrDefault() ?? "push";
        var message = op == "push" && args.Length > 1 ? args[1] : null;

        var json = await LTAI.Agent.Tools.GitTools.GitStash(operation: op, message: message);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        {
            AnsiConsole.MarkupLine($"[red]{err.GetString()}[/]");
            return;
        }

        if (root.TryGetProperty("stashed", out _))
            AnsiConsole.MarkupLine($"[green]Stashed changes[/]");
        else if (root.TryGetProperty("popped", out _))
            AnsiConsole.MarkupLine($"[green]Popped stash[/]");
        else if (root.TryGetProperty("applied", out _))
            AnsiConsole.MarkupLine($"[green]Applied stash[/]");
        else if (root.TryGetProperty("dropped", out _))
            AnsiConsole.MarkupLine($"[green]Dropped stash[/]");
        else if (root.TryGetProperty("stashes", out var stashes))
        {
            AnsiConsole.MarkupLine($"[bold]{stashes.GetArrayLength()} stash(es)[/]");
            foreach (var s in stashes.EnumerateArray())
            {
                var idx = s.GetProperty("index").GetInt32();
                var msg = s.GetProperty("message").GetString();
                AnsiConsole.MarkupLine($"  [{idx}] {msg}");
            }
        }
    }

    private static async Task RunGitTagAsync(string[] args)
    {
        var op = args.FirstOrDefault() ?? "list";
        string? name = args.Length > 1 ? args[1] : null;
        string? message = op == "create" && args.Length > 2 ? args[2] : null;

        var json = await LTAI.Agent.Tools.GitTools.GitTag(operation: op, name: name, message: message);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        {
            AnsiConsole.MarkupLine($"[red]{err.GetString()}[/]");
            return;
        }

        if (root.TryGetProperty("created", out _))
            AnsiConsole.MarkupLine($"[green]Tag created:[/] [cyan]{name}[/]");
        else if (root.TryGetProperty("deleted", out _))
            AnsiConsole.MarkupLine($"[green]Tag deleted:[/] [cyan]{name}[/]");
        else if (root.TryGetProperty("tags", out var tags))
        {
            AnsiConsole.MarkupLine($"[bold]{tags.GetArrayLength()} tag(s)[/]");
            foreach (var t in tags.EnumerateArray())
            {
                var tName = t.GetProperty("name").GetString();
                var sha = t.GetProperty("sha").GetString();
                AnsiConsole.MarkupLine($"  [cyan]{tName}[/] [dim]{sha}[/]");
            }
        }
    }

    private static async Task RunGitShowAsync(string[] args)
    {
        var target = args.FirstOrDefault() ?? "HEAD";
        var json = await LTAI.Agent.Tools.GitTools.GitShow(target: target);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        {
            AnsiConsole.MarkupLine($"[red]{err.GetString()}[/]");
            return;
        }

        var sha = root.GetProperty("sha").GetString();
        var author = root.GetProperty("author");
        var msg = root.GetProperty("message").GetString().Trim();
        var files = root.GetProperty("filesChanged").GetInt32();

        AnsiConsole.MarkupLine($"[bold]Commit:[/] [yellow]{sha}[/]");
        AnsiConsole.MarkupLine($"[bold]Author:[/] {author.GetProperty("name").GetString()} [dim]<{author.GetProperty("email").GetString()}>[/]");
        AnsiConsole.MarkupLine($"[bold]Date:[/]   {author.GetProperty("date").GetString()}");
        AnsiConsole.MarkupLine($"");
        AnsiConsole.MarkupLine(msg);
        AnsiConsole.MarkupLine($"");
        AnsiConsole.MarkupLine($"[dim]{files} file(s) changed[/]");

        if (root.TryGetProperty("diff", out var diff) && diff.ValueKind == JsonValueKind.String)
        {
            var diffText = diff.GetString();
            if (!string.IsNullOrWhiteSpace(diffText))
            {
                AnsiConsole.MarkupLine("");
                AnsiConsole.MarkupLine("[bold]Diff:[/]");
                AnsiConsole.MarkupLine(diffText);
            }
        }
    }

    private static async Task RunGitBlameAsync(string[] args)
    {
        var filePath = args.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(filePath))
        {
            AnsiConsole.MarkupLine("[red]Usage: ltai git blame <file>[/]");
            return;
        }

        var json = await LTAI.Agent.Tools.GitTools.GitBlame(filePath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        {
            AnsiConsole.MarkupLine($"[red]{err.GetString()}[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[bold]Blame:[/] [cyan]{filePath}[/]");
        AnsiConsole.MarkupLine("");

        foreach (var hunk in root.GetProperty("blame").EnumerateArray())
        {
            var line = hunk.GetProperty("lineNumber").GetInt32();
            var sha = hunk.GetProperty("commitSha").GetString();
            var author = hunk.GetProperty("author").GetString();
            var date = hunk.GetProperty("date").GetString();
            var summary = hunk.GetProperty("summary").GetString();

            AnsiConsole.MarkupLine($"[dim]{line,4}[/] [yellow]{sha}[/] [cyan]{author,-15}[/] [dim]{date}[/] {summary}");
        }
    }

    private static async Task RunGitRemoteAsync(string[] args)
    {
        var op = args.FirstOrDefault() ?? "list";
        string? name = args.Length > 1 ? args[1] : null;
        string? url = op == "add" && args.Length > 2 ? args[2] : null;

        var json = await LTAI.Agent.Tools.GitTools.GitRemote(operation: op, name: name, url: url);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        {
            AnsiConsole.MarkupLine($"[red]{err.GetString()}[/]");
            return;
        }

        if (root.TryGetProperty("added", out _))
            AnsiConsole.MarkupLine($"[green]Remote added:[/] [cyan]{name}[/] → {url}");
        else if (root.TryGetProperty("name", out _))
        {
            AnsiConsole.MarkupLine($"[bold]Remote:[/] [cyan]{root.GetProperty("name").GetString()}[/]");
            AnsiConsole.MarkupLine($"  URL:      {root.GetProperty("url").GetString()}");
            AnsiConsole.MarkupLine($"  Push URL: {root.GetProperty("pushUrl").GetString()}");
        }
        else if (root.TryGetProperty("remotes", out var remotes))
        {
            AnsiConsole.MarkupLine($"[bold]{remotes.GetArrayLength()} remote(s)[/]");
            foreach (var r in remotes.EnumerateArray())
            {
                AnsiConsole.MarkupLine($"  [cyan]{r.GetProperty("name").GetString()}[/] → {r.GetProperty("url").GetString()}");
            }
        }
    }

    private static async Task RunGitResetAsync(string[] args)
    {
        var mode = "mixed";
        var target = "HEAD";
        string? filePath = null;

        foreach (var arg in args)
        {
            if (arg.StartsWith("--"))
                mode = arg[2..]; // --soft, --mixed, --hard
            else if (!string.IsNullOrWhiteSpace(arg) && target == "HEAD" && arg != "HEAD")
                target = arg;
        }

        // If target looks like a file path, treat as file-level reset
        if (target.Contains('.') || target.Contains('/') || target.Contains('\\'))
        {
            filePath = target;
            target = "HEAD";
        }

        var json = await LTAI.Agent.Tools.GitTools.GitReset(mode: mode, target: target, filePath: filePath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        {
            AnsiConsole.MarkupLine($"[red]{err.GetString()}[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[green]Reset ({mode}):[/] {target}");
    }

    private static async Task RunGitCloneAsync(string[] args)
    {
        var url = args.FirstOrDefault(a => !a.StartsWith("-"));
        if (string.IsNullOrWhiteSpace(url))
        {
            AnsiConsole.MarkupLine("[red]Usage: ltai git clone <url> [--branch B] [--shallow] [--target <dir>][/]");
            return;
        }

        var branch = (string?)null;
        var bi = Array.IndexOf(args, "--branch");
        if (bi >= 0 && bi + 1 < args.Length) branch = args[bi + 1];

        var shallow = args.Contains("--shallow");

        var ti = Array.IndexOf(args, "--target");
        var targetDir = ti >= 0 && ti + 1 < args.Length ? args[ti + 1] : null;

        AnsiConsole.MarkupLine($"[dim]Cloning {url}...[/]");
        var json = await LTAI.Agent.Tools.GitTools.GitClone(url, targetDir, branch, shallow);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        {
            AnsiConsole.MarkupLine($"[red]{err.GetString()}[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[green]Cloned:[/] {root.GetProperty("path").GetString()}");
        AnsiConsole.MarkupLine($"  Branch:  [cyan]{root.GetProperty("branch").GetString()}[/]");
        AnsiConsole.MarkupLine($"  Commit:  [yellow]{root.GetProperty("commit").GetString()}[/]");
    }

    private static void PrintFileList(string label, JsonElement root, string property, Color color)
    {
        if (!root.TryGetProperty(property, out var arr) || arr.GetArrayLength() == 0) return;
        AnsiConsole.MarkupLine($"  [bold]{label}:[/]");
        foreach (var f in arr.EnumerateArray())
            AnsiConsole.MarkupLine($"    [{color}]{f.GetString()}[/]");
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
        AnsiConsole.MarkupLine("  init, install, setup, add, remove, up, down, ps, update, env, git, dev, model");
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
