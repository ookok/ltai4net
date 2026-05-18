using System.Text;
using Spectre.Console;

namespace LTAI.TUI;

public sealed class InnovationViews
{
    private readonly List<(string query, string response, DateTime time)> _history = new();
    private readonly List<ThoughtNode> _thoughtChain = new();
    private bool _showThoughtChain;

    public void RecordInteraction(string query, string response)
    {
        _history.Add((query, response, DateTime.Now));
        if (_history.Count > 100) _history.RemoveAt(0);
    }

    public void AddThought(string step, string content, ThoughtType type = ThoughtType.Reasoning)
    {
        _thoughtChain.Add(new ThoughtNode
        {
            Step = step,
            Content = content,
            Type = type,
            Timestamp = DateTime.Now
        });

        if (_thoughtChain.Count > 20) _thoughtChain.RemoveAt(0);
    }

    public void ToggleThoughtChain() => _showThoughtChain = !_showThoughtChain;

    public IReadOnlyList<ThoughtDisplay> GetThoughts() =>
        _thoughtChain.Select(t => new ThoughtDisplay
        {
            Step = t.Step,
            Content = t.Content,
            Type = t.Type.ToString()
        }).ToList().AsReadOnly();

    public IRenderable RenderThoughtChain()
    {
        if (!_showThoughtChain || _thoughtChain.Count == 0)
            return new Markup("[grey](Press T to show thought chain)[/]");

        var sb = new StringBuilder();
        sb.AppendLine("[cyan bold]   Chain of Thought[/]");
        sb.AppendLine();

        for (var i = 0; i < _thoughtChain.Count; i++)
        {
            var thought = _thoughtChain[i];
            var indent = new string(' ', Math.Min(i * 2, 20));
            var icon = thought.Type switch
            {
                ThoughtType.Reasoning => "[yellow]💭[/]",
                ThoughtType.Action => "[cyan]🔧[/]",
                ThoughtType.Observation => "[green]👁[/]",
                ThoughtType.Reflection => "[magenta]🪞[/]",
                _ => "[grey]•[/]"
            };

            sb.AppendLine($"{indent}{icon} [white]{thought.Step}[/]");
            if (!string.IsNullOrEmpty(thought.Content))
                sb.AppendLine($"{indent}  [grey]{thought.Content[..Math.Min(thought.Content.Length, 80)]}[/]");
        }

        return new Panel(new Markup(sb.ToString().TrimEnd()))
        {
            Header = new PanelHeader("[cyan]Thinking Process[/]"),
            Border = BoxBorder.Rounded
        };
    }

    public IRenderable RenderKnowledgePreview(string query, List<string>? knowledgeItems = null)
    {
        if (knowledgeItems == null || knowledgeItems.Count == 0)
            return new Text("");

        var sb = new StringBuilder();
        sb.AppendLine("[yellow bold]   Knowledge Retrieved[/]");

        foreach (var item in knowledgeItems.Take(5))
        {
            var preview = item.Length > 100 ? item[..97] + "..." : item;
            sb.AppendLine($"  [grey]•[/] [white]{preview}[/]");
        }

        return new Panel(new Markup(sb.ToString().TrimEnd()))
        {
            Header = new PanelHeader("[yellow]Knowledge Graph[/]"),
            Border = BoxBorder.Rounded
        };
    }

    public IRenderable RenderHistoryReplay(int maxItems = 5)
    {
        if (_history.Count == 0)
            return new Markup("[grey](No history yet)[/]");

        var sb = new StringBuilder();
        foreach (var (query, response, time) in _history.TakeLast(maxItems))
        {
            sb.AppendLine($"[grey]{time:HH:mm}[/] [green]Q:[/] {query[..Math.Min(query.Length, 60)]}");
            sb.AppendLine($"   [cyan]A:[/] {response[..Math.Min(response.Length, 80)]}");
            sb.AppendLine();
        }

        return new Panel(new Markup(sb.ToString().TrimEnd()))
        {
            Header = new PanelHeader("[blue]History Replay[/]"),
            Border = BoxBorder.Rounded
        };
    }

    public IRenderable RenderInnovationSuggestions()
    {
        return new Panel(new Markup("""
            [bold cyan]Innovation Ideas[/]

            [yellow]T[/] Toggle thought chain visualization
            [yellow]K[/] View knowledge graph connections
            [yellow]H[/] Replay session history
            [yellow]M[/] Memory consolidation (auto)
            [yellow]E[/] Export session summary

            [grey]Chain-of-thought records reasoning steps
            Knowledge graph shows retrieved context
            History replay navigates past conversations[/]
            """))
        {
            Header = new PanelHeader("[cyan]Suggestions[/]"),
            Border = BoxBorder.Rounded
        };
    }
}

public sealed class ThoughtNode
{
    public string Step { get; init; } = "";
    public string Content { get; init; } = "";
    public ThoughtType Type { get; init; }
    public DateTime Timestamp { get; init; }
}

public enum ThoughtType { Reasoning, Action, Observation, Reflection }

public sealed class ThoughtDisplay
{
    public string Step { get; init; } = "";
    public string Content { get; init; } = "";
    public string Type { get; init; } = "";
}
