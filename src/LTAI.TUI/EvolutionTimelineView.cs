using Spectre.Console;
using Spectre.Console.Rendering;
using LTAI.AI.Governors;
using LTAI.Tools.Crawler;

namespace LTAI.TUI;

public sealed class EvolutionTimelineView
{
    private readonly ICrossRunEvolutionStore? _store;

    public EvolutionTimelineView(ICrossRunEvolutionStore? store = null)
    {
        _store = store;
    }

    public IRenderable Render()
    {
        if (_store == null)
            return new Markup("[grey]Evolution store not available.[/]");

        var panel = new Panel(BuildTimeline());
        panel.Header = new PanelHeader("[cyan]📈 Evolution Timeline[/]");
        panel.Border = BoxBorder.Rounded;
        return panel;
    }

    private IRenderable BuildTimeline()
    {
        var tree = new Tree("[bold cyan]Cross-Run Evolution[/]");

        try
        {
            var lessons = _store.GetActiveLessons(30);
            if (lessons.Count == 0)
            {
                tree.AddNode("[grey]No evolution lessons recorded[/]");
                return tree;
            }

            var byCategory = lessons.GroupBy(l => l.Category);
            foreach (var group in byCategory)
            {
                var catNode = tree.AddNode($"[yellow]{group.Key}[/] ([white]{group.Count()}[/])");
                foreach (var lesson in group.Take(5))
                {
                    var severityBar = lesson.Severity >= 0.8f ? "[red]█[/]" :
                                     lesson.Severity >= 0.5f ? "[yellow]█[/]" : "[green]█[/]";
                    catNode.AddNode($"{severityBar} [white]{lesson.Summary?.Truncate(70)}[/] [dim]s={lesson.Severity:F1}[/]");
                }
            }
        }
        catch { tree.AddNode("[grey]Evolution data unavailable[/]"); }

        return tree;
    }
}
