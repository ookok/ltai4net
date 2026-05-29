using Spectre.Console;
using LTAI.Knowledge.Core;

namespace LTAI.Cli;

partial class Program
{
    // ════════════════════════════════════════════════════════════════
    // ltai dev [setup]
    // ════════════════════════════════════════════════════════════════

    private static async Task RunDevAsync(string[] args)
    {
        var sub = args.FirstOrDefault()?.ToLowerInvariant();
        if (sub == "setup") { await RunDevSetupAsync(); return; }
        await RunDevCheckAsync();
    }

    private static Task RunDevCheckAsync()
    {
        AnsiConsole.Write(new Rule("[bold cyan]LTAI Development Environment Check[/]").RuleStyle(Style.Plain));
        var allOk = true;

        AnsiConsole.Markup("[bold].NET SDK 10.0[/] ");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("dotnet", "--version")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(5000);
            var output = proc?.StandardOutput.ReadToEnd()?.Trim() ?? "";
            var verMatch = System.Text.RegularExpressions.Regex.Match(output, @"^(\d+)\.\d+\.\d+");

            if (verMatch.Success && int.TryParse(verMatch.Groups[1].Value, out var major) && major >= 10)
                AnsiConsole.MarkupLine($"[green]✓ {output}[/]");
            else
            {
                AnsiConsole.MarkupLine($"[red]✗ .NET 10.0 SDK required. Found: {output}[/]");
                AnsiConsole.MarkupLine("[dim]  Install: https://dotnet.microsoft.com/download/dotnet/10.0[/]");
                allOk = false;
            }
        }
        catch { AnsiConsole.MarkupLine("[red]✗ dotnet CLI not found in PATH[/]"); allOk = false; }

        AnsiConsole.Markup("[bold]Git repository[/] ");
        try
        {
            var discovered = LibGit2Sharp.Repository.Discover(Directory.GetCurrentDirectory());
            AnsiConsole.MarkupLine(!string.IsNullOrEmpty(discovered) ? "[green]✓ found[/]" : "[yellow]⚠ not a git repository (optional)[/]");
        }
        catch { AnsiConsole.MarkupLine("[yellow]⚠ not a git repository (optional)[/]"); }

        AnsiConsole.Markup("[bold]LTAI_WORKSPACE[/] ");
        var workspace = OptionService.Get("LTAI_WORKSPACE") ?? Environment.GetEnvironmentVariable("LTAI_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace))
            AnsiConsole.MarkupLine(Directory.Exists(workspace) ? $"[green]✓ {workspace}[/]" : $"[yellow]⚠ set but directory missing: {workspace}[/]");
        else
        {
            var cwd = Directory.GetCurrentDirectory();
            var hasSl = File.Exists(Path.Combine(cwd, "LTAI.sln"));
            var hasSrc = Directory.Exists(Path.Combine(cwd, "src"));
            if (hasSl && hasSrc) AnsiConsole.MarkupLine($"[green]✓ (auto-detected: {cwd})[/]");
            else { AnsiConsole.MarkupLine("[yellow]⚠ not set — run 'ltai dev setup'[/]"); allOk = false; }
        }

        AnsiConsole.Markup("[bold]LTAI_L1_API_KEY[/] ");
        var l1Key = Environment.GetEnvironmentVariable("LTAI_L1_API_KEY") ?? CliConfig.Load().L1ApiKey;
        AnsiConsole.MarkupLine(!string.IsNullOrWhiteSpace(l1Key) ? $"[green]✓ {MaskSecret(l1Key)}[/]" : "[dim]○ (not set, L1 will use L2 fallback)[/]");

        AnsiConsole.Markup("[bold]LTAI_L2_API_KEY[/] ");
        var l2Key = Environment.GetEnvironmentVariable("LTAI_L2_API_KEY") ?? CliConfig.Load().L2ApiKey;
        if (!string.IsNullOrWhiteSpace(l2Key))
            AnsiConsole.MarkupLine($"[green]✓ {MaskSecret(l2Key)}[/]");
        else
        {
            var providerKeys = new[] { "DEEPSEEK_API_KEY", "OPENAI_API_KEY", "DASHSCOPE_API_KEY", "ANTHROPIC_API_KEY" };
            var found = providerKeys.FirstOrDefault(k => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(k)));
            if (found != null)
                AnsiConsole.MarkupLine($"[green]✓ via {found} ({MaskSecret(Environment.GetEnvironmentVariable(found)!)})[/]");
            else
            {
                AnsiConsole.MarkupLine("[red]✗ no API key configured[/]");
                AnsiConsole.MarkupLine("[dim]  Run 'ltai env set LTAI_L2_API_KEY <key>'[/]");
                allOk = false;
            }
        }

        AnsiConsole.Write(allOk
            ? new Markup("\n[bold green]All checks passed! Ready to develop.[/]\n[dim]Run 'ltai up' to start the TUI.[/]")
            : new Markup("\n[bold yellow]Some checks failed. Run 'ltai dev setup' to auto-configure.[/]"));

        return Task.CompletedTask;
    }

    private static async Task RunDevSetupAsync()
    {
        AnsiConsole.Write(new Rule("[bold cyan]LTAI Dev Environment Setup[/]").RuleStyle(Style.Plain));
        var cwd = Directory.GetCurrentDirectory();
        var config = CliConfig.Load();
        var changed = false;

        if (string.IsNullOrWhiteSpace(config.WorkspaceRoot))
        { config.WorkspaceRoot = cwd; AnsiConsole.MarkupLine($"[green]✓ LTAI_WORKSPACE → {cwd}[/]"); changed = true; }
        else AnsiConsole.MarkupLine($"[dim]LTAI_WORKSPACE already set: {config.WorkspaceRoot}[/]");

        if (string.IsNullOrWhiteSpace(config.InstallPath) || config.InstallPath == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ltai"))
        { config.InstallPath = Path.Combine(cwd, ".ltai"); AnsiConsole.MarkupLine($"[green]✓ LTAI_HOME → {config.InstallPath}[/]"); changed = true; }

        if (string.IsNullOrWhiteSpace(config.L2ApiKey))
        {
            var existingKey = Environment.GetEnvironmentVariable("LTAI_L2_API_KEY") ?? Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (!string.IsNullOrWhiteSpace(existingKey))
            { config.L2ApiKey = existingKey; AnsiConsole.MarkupLine($"[green]✓ LTAI_L2_API_KEY from env ({MaskSecret(existingKey)})[/]"); changed = true; }
            else
            {
                config.L2ApiKey = AnsiConsole.Prompt(new TextPrompt<string>("L2 API Key (DeepSeek/OpenAI)").AllowEmpty().Secret()) ?? "";
                if (!string.IsNullOrWhiteSpace(config.L2ApiKey)) changed = true;
            }
        }

        AnsiConsole.Markup("[bold]dotnet restore[/] ");
        try
        {
            var sln = FindSolutionFile();
            if (sln != null)
            {
                using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", $"restore \"{sln}\"")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
                if (proc != null) { await proc.WaitForExitAsync(); AnsiConsole.MarkupLine(proc.ExitCode == 0 ? "[green]✓ packages restored[/]" : "[yellow]⚠ restore completed with warnings[/]"); }
            }
        }
        catch { AnsiConsole.MarkupLine("[yellow]⚠ restore failed[/]"); }

        if (changed) { config.Save(); config.SetEnv(); AnsiConsole.MarkupLine($"[green]✓ Config saved to {CliConfig.ConfigPath}[/]"); }
        AnsiConsole.Write(new Markup("\n[bold green]Dev environment configured![/]\n[dim]Run 'ltai dev' to verify, 'ltai up' to start.[/]"));
    }

    // ════════════════════════════════════════════════════════════════
    // ltai debug — E2E pipeline trace & batch test runner
    // ════════════════════════════════════════════════════════════════

    private static async Task RunDebugAsync(string[] args)
    {
        var queryIdx = Array.IndexOf(args, "--query");
        var batch = args.Any(a => a is "--batch" or "-b");
        var layer = args.SkipWhile(a => a != "--layer").Skip(1).FirstOrDefault();
        var count = args.SkipWhile(a => a != "--count").Skip(1).FirstOrDefault();
        var report = args.Any(a => a is "--report" or "-r");

        if (batch) { await DebugMode.RunBatchAsync(layer ?? "all").ConfigureAwait(false); return; }

        if (queryIdx >= 0 && queryIdx + 1 < args.Length)
        {
            var query = args[queryIdx + 1];
            var countVal = int.TryParse(count, out var c) ? c : 1;
            await DebugMode.RunAsync(query, countVal, null, null, report).ConfigureAwait(false);
            return;
        }

        var defaultCount = int.TryParse(count, out var n) ? n : 20;
        await DebugMode.RunAsync(null, defaultCount, null, null, report).ConfigureAwait(false);
    }
}
