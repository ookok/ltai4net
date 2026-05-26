using System.Reflection;
using Spectre.Console;
using Spectre.Console.Rendering;
using LTAI.Core.Interfaces;
using LTAI.Tools.Crawler;

namespace LTAI.TUI;

public sealed class ParliamentView
{
    private readonly IParliamentBridge? _bridge;

    public ParliamentView(IParliamentBridge? bridge = null)
    {
        _bridge = bridge;
    }

    public IRenderable Render()
    {
        if (_bridge == null)
            return new Markup("[grey]Parliament bridge not available.[/]");

        var panel = new Panel(BuildDebate());
        panel.Header = new PanelHeader("[blue]🏛 Parliament Debate[/]");
        panel.Border = BoxBorder.Rounded;
        return panel;
    }

    private IRenderable BuildDebate()
    {
        var tree = new Tree("[bold blue]Parliament Deliberation[/]");

        try
        {
            var isAvailable = (bool)(_bridge.GetType().GetProperty("IsAvailable")?.GetValue(_bridge) ?? false);
            tree.AddNode($"[dim]Status: {(isAvailable ? "[green]Active[/]" : "[grey]Inactive[/]")}[/]");

            var sessionsField = _bridge.GetType().GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance);
            if (sessionsField != null)
            {
                var sessions = sessionsField.GetValue(_bridge) as System.Collections.IDictionary;
                if (sessions != null && sessions.Count > 0)
                {
                    foreach (System.Collections.DictionaryEntry entry in sessions)
                    {
                        var val = entry.Value;
                        var queryProp = val?.GetType().GetProperty("Query");
                        var resultProp = val?.GetType().GetProperty("Result");
                        var query = queryProp?.GetValue(val)?.ToString()?.Truncate(60) ?? "?";
                        var result = resultProp?.GetValue(val)?.ToString()?.Truncate(60) ?? "?";
                        tree.AddNode($"[cyan]Q:[/] {query}");
                        tree.AddNode($"[green]A:[/] {result}");
                    }
                }
                else
                    tree.AddNode("[grey]No deliberation sessions recorded[/]");
            }
        }
        catch { tree.AddNode("[grey]Parliament data unavailable[/]"); }

        return tree;
    }
}
