using System.Diagnostics;
using LTAI.Agent;
using LTAI.Agent.Resilience;
using LTAI.Agent.Models;
using LTAI.AI;
using LTAI.Core;
using LTAI.Core.Configuration;
using LTAI.Knowledge.Vector;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace LTAI.Cli;

internal static class AutoFixMode
{
    public static async Task RunAsync(string? target, string args, int maxAttempts, bool analyzeOnly,
        bool scanOnly, bool guiMode, int timeoutMs, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(target))
        {
            AnsiConsole.MarkupLine("[red]Error: --target is required[/]");
            return;
        }

        var targetPath = Path.GetFullPath(target);
        if (!Directory.Exists(targetPath) && !File.Exists(targetPath))
        {
            AnsiConsole.MarkupLine($"[red]Error: Target not found: {targetPath}[/]");
            return;
        }

        AnsiConsole.MarkupLine("[bold cyan]=== LTAI Auto-Fix (LLM-Driven Debugging) ===[/]\n");

        var baseDir = AppContext.BaseDirectory;
        var configPath = Path.Combine(baseDir, "appsettings.json");

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(configPath, optional: true, reloadOnChange: false)
            .Build();

        var services = new ServiceCollection();
        services.Configure<LTAIOptions>(configuration.GetSection(LTAIOptions.SectionName));
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddLTAICore();
        services.AddLTAIVectorAuto();
        services.AddLTAIAI();
        services.AddLTAIAgent();
        services.AddLTAITreeLLM();

        using var sp = services.BuildServiceProvider();
        var debugLoop = sp.GetRequiredService<DebugLoop>();

        var level = analyzeOnly ? DebugLevel.Analyze : DebugLevel.SemiAuto;

        if (scanOnly)
        {
            await RunStaticAnalysisAsync(targetPath, debugLoop, ct).ConfigureAwait(false);
            return;
        }

        AnsiConsole.MarkupLine($"Target: [yellow]{targetPath}[/]");
        AnsiConsole.MarkupLine($"Args: [dim]{args}[/]");
        AnsiConsole.MarkupLine($"Max attempts: [green]{maxAttempts}[/]");
        AnsiConsole.MarkupLine($"Mode: [{(analyzeOnly ? "yellow" : "cyan")}]{(analyzeOnly ? "Trace & Analyze" : "Auto-Fix with Root-Cause Tracing")}[/]{(guiMode ? " [grey](GUI mode)[/]" : "")}");
        AnsiConsole.MarkupLine($"Timeout: [dim]{timeoutMs}ms[/]");
        AnsiConsole.MarkupLine(new string('-', 60));

        try
        {
            AnsiConsole.MarkupLine("[bold]Running initial check...[/]");
            var session = await debugLoop.DebugAsync(targetPath, args, level, maxAttempts, timeoutMs, ct).ConfigureAwait(false);

            AnsiConsole.MarkupLine(new string('-', 60));
            if (session.Fixed)
            {
                AnsiConsole.MarkupLine("[bold green]FIXED![/] Target runs successfully.");
            }
            else if (session.Escalated)
            {
                AnsiConsole.MarkupLine("[bold red]ESCALATED[/] — requires human intervention.");
            }
            else
            {
                AnsiConsole.MarkupLine("[bold yellow]UNRESOLVED[/] — max attempts reached without success.");
            }
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine($"[bold]Session:[/] {session.Id}");
            AnsiConsole.MarkupLine($"[bold]Duration:[/] {session.TotalDurationMs:F0}ms");
            AnsiConsole.MarkupLine($"[bold]Attempts:[/] {session.Attempts.Count}");

            foreach (var attempt in session.Attempts)
            {
                var status = attempt.Result switch
                {
                    AttemptResult.Fixed => "[green]FIXED[/]",
                    AttemptResult.Partial => "[yellow]PARTIAL[/]",
                    AttemptResult.Worse => "[red]WORSE[/]",
                    AttemptResult.Unchanged => "[dim]UNCHANGED[/]",
                    AttemptResult.Hitl => "[orange3]NEEDS HUMAN[/]",
                    _ => attempt.Result.ToString()
                };

                var tokens = attempt.LlmTokens > 0 ? $" ({attempt.LlmTokens} est. tokens)" : "";
                AnsiConsole.MarkupLine($"  Attempt {attempt.AttemptNumber}: {status}{tokens} ({attempt.DurationMs:F0}ms)");

                if (attempt.Error.ExceptionType != "UnknownError")
                    AnsiConsole.MarkupLine($"    [dim]{attempt.Error.ExceptionType}: {Markup.Escape(attempt.Error.ExceptionMessage[..Math.Min(attempt.Error.ExceptionMessage.Length, 120)])}[/]");
            }

            if (!session.Fixed && session.Escalated)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[dim]Tip: Review the error manually or increase --attempts for more retries.[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Fatal error: {Markup.Escape(ex.Message)}[/]");
        }
    }

    private static async Task RunStaticAnalysisAsync(string filePath, DebugLoop debugLoop, CancellationToken ct)
    {
        AnsiConsole.MarkupLine("[bold cyan]=== LTAI Proactive Static Analysis ===[/]\n");
        AnsiConsole.MarkupLine($"Scanning: [yellow]{filePath}[/]");
        AnsiConsole.MarkupLine("[dim]LLM traces execution flow through code — no logs needed[/]");
        AnsiConsole.MarkupLine(new string('-', 60));

        var issues = await debugLoop.AnalyzeAsync(filePath, ct).ConfigureAwait(false);

        if (issues.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No issues found.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[bold]Found {issues.Count} potential issues:[/]\n");

        foreach (var issue in issues.OrderByDescending(i => SeverityWeight(i.Severity)))
        {
            var icon = issue.Severity.ToUpperInvariant() switch
            {
                "CRITICAL" => "[red]CRIT[/]",
                "HIGH" => "[orange3]HIGH[/]",
                "MEDIUM" => "[yellow]MED [/]",
                _ => "[dim]LOW [/]"
            };

            AnsiConsole.MarkupLine($"  {icon} [bold]{issue.Category}[/] at line {issue.LineNumber}:");
            AnsiConsole.MarkupLine($"       [dim]{Markup.Escape(issue.Description)}[/]");

            if (!string.IsNullOrEmpty(issue.SuggestedFix))
                AnsiConsole.MarkupLine($"       [green]Fix: {Markup.Escape(issue.SuggestedFix[..Math.Min(issue.SuggestedFix.Length, 150)])}[/]");

            AnsiConsole.MarkupLine("");
        }

        AnsiConsole.MarkupLine("[dim]Run 'ltai auto-fix --target <file>' to attempt automatic fixes.[/]");
    }

    private static int SeverityWeight(string severity) => severity.ToUpperInvariant() switch
    {
        "CRITICAL" => 4, "HIGH" => 3, "MEDIUM" => 2, _ => 1
    };
}
