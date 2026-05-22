using Spectre.Console.Rendering;
using Spectre.Console;

namespace LTAI.TUI;

public sealed class ContextWindowView
{
    private const int MaxContext = 128000;

    public IRenderable Render(SessionTracker session)
    {
        var systemPrompt = 2000;
        var history = Math.Min(session.InputTokens + session.OutputTokens, MaxContext - 4000);
        var knowledge = Math.Min(session.ContextWindowUsed - systemPrompt - history, MaxContext / 4);
        var userInput = 500;
        var generation = Math.Min(session.OutputTokens > 0 ? session.OutputTokens * 2 : 4096, 16000);
        var free = Math.Max(0, MaxContext - systemPrompt - history - knowledge - userInput - generation);

        var used = systemPrompt + history + knowledge + userInput + generation;
        var usedPct = (double)used / MaxContext * 100;

        var chart = new BreakdownChart()
            .Width(60)
            .AddItem("System", systemPrompt, Color.Grey)
            .AddItem("History", history, Color.Blue)
            .AddItem("Knowledge", knowledge, Color.Yellow)
            .AddItem("Input", userInput, Color.Green)
            .AddItem("Generation", generation, Color.Cyan1)
            .AddItem("Free", free, Color.Grey37);

        var warn = usedPct > 80
            ? $"[red]⚠ {usedPct:F0}% used — consider compressing history[/]"
            : usedPct > 60
                ? $"[yellow]⚡ {usedPct:F0}% used[/]"
                : $"[green]{usedPct:F0}% used[/]";

        var panel = new Panel(new Rows(
            new Markup($"[cyan]Context Window:[/] {used}/{MaxContext} tokens {warn}"),
            chart))
        {
            Header = new PanelHeader("[cyan]Token Budget[/]"),
            Border = BoxBorder.Rounded
        };

        return panel;
    }
}
