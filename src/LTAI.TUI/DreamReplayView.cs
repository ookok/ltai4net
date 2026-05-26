using System.Reflection;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Rendering;
using LTAI.AI.Governors;
using LTAI.Tools.Crawler;

namespace LTAI.TUI;

public sealed class DreamReplayView
{
    private readonly DreamCycle? _dreamCycle;
    private readonly string? _dreamLogPath;

    public DreamReplayView(DreamCycle? dreamCycle = null)
    {
        _dreamCycle = dreamCycle;
        if (dreamCycle != null)
        {
            try
            {
                var pathField = typeof(DreamCycle).GetField("_dreamLogPath", BindingFlags.NonPublic | BindingFlags.Instance);
                _dreamLogPath = pathField?.GetValue(dreamCycle) as string;
            }
            catch { }
        }
    }

    public IRenderable Render()
    {
        if (_dreamCycle == null)
            return new Markup("[grey]DreamCycle not available.[/]");

        var panel = new Panel(BuildDreamLog());
        panel.Header = new PanelHeader("[magenta]🌙 Dream Replay[/]");
        panel.Border = BoxBorder.Rounded;
        return panel;
    }

    private IRenderable BuildDreamLog()
    {
        var tree = new Tree("[bold magenta]Dream Reflections[/]");

        if (_dreamLogPath != null && File.Exists(_dreamLogPath))
        {
            try
            {
                var json = File.ReadAllText(_dreamLogPath);
                var entries = JsonSerializer.Deserialize<List<JsonElement>>(json);
                if (entries != null)
                {
                    foreach (var entry in entries.Take(15).Reverse())
                    {
                        var timestamp = entry.TryGetProperty("timestamp", out var ts) ? ts.GetString() : "?";
                        var summary = entry.TryGetProperty("summary", out var s) ? s.GetString()?.Truncate(80) : "?";
                        tree.AddNode($"[dim]{timestamp}[/] [white]{summary}[/]");
                    }
                }
            }
            catch { tree.AddNode("[grey]Dream log parse error[/]"); }
        }
        else
            tree.AddNode("[grey]No dream log found[/]");

        var moonPhase = (DateTime.Now.Day % 8) switch
        {
            0 => "🌑", 1 => "🌒", 2 => "🌓", 3 => "🌔",
            4 => "🌕", 5 => "🌖", 6 => "🌗", _ => "🌘"
        };
        tree.AddNode($"[dim]Current moon phase: {moonPhase} (cycle day {DateTime.Now.Day % 28})[/]");

        return tree;
    }
}
