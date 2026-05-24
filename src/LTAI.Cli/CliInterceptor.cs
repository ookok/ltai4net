using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace LTAI.Cli;

public sealed class CliInterceptor : ICommandInterceptor
{
    private Stopwatch? _stopwatch;

    public void Intercept(CommandContext context, CommandSettings settings)
    {
        _stopwatch = Stopwatch.StartNew();
    }

    public void InterceptResult(CommandContext context, CommandSettings settings, ref int result)
    {
        _stopwatch?.Stop();
        if (result != 0)
        {
            AnsiConsole.MarkupLine($"[red]Command '{context.Name}' failed with exit code {result} ({_stopwatch?.ElapsedMilliseconds}ms)[/]");
        }
        else if (_stopwatch?.ElapsedMilliseconds > 1000)
        {
            AnsiConsole.MarkupLine($"[dim]Completed in {_stopwatch?.ElapsedMilliseconds}ms[/]");
        }
    }
}
