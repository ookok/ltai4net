using Spectre.Console;
using Spectre.Console.Rendering;
using LTAI.Agent.Workflows;

namespace LTAI.TUI;

public sealed class SwarmView
{
    private readonly LTAICoordinator? _coordinator;

    public SwarmView(LTAICoordinator? coordinator = null)
    {
        _coordinator = coordinator;
    }

    public IRenderable Render()
    {
        var panel = new Panel(BuildSwarm());
        panel.Header = new PanelHeader("[yellow]🐝 Agent Swarm[/]");
        panel.Border = BoxBorder.Rounded;
        panel.BorderColor(Color.Yellow);
        return panel;
    }

    private IRenderable BuildSwarm()
    {
        var tree = new Tree("[bold yellow]🐝 Coordinator[/]");
        if (_coordinator?.ActiveSessions != null)
        {
            var sessions = _coordinator.ActiveSessions.Values.OrderBy(s => s.CreatedAt).ToList();
            foreach (var s in sessions)
            {
                var statusIcon = s.CompletedAt != null
                    ? (s.Result != null ? "✅" : "❌")
                    : "🟢";
                var elapsed = s.CompletedAt != null
                    ? $"[grey]{(s.CompletedAt.Value - s.CreatedAt).TotalSeconds:F1}s[/]"
                    : $"[yellow]{GetSpinner()}[/] {(DateTime.UtcNow - s.CreatedAt).TotalSeconds:F1}s";

                var node = tree.AddNode($"{statusIcon} [cyan]{Markup.Escape(s.AgentName)}[/] [dim]({Markup.Escape(s.Role)})[/] {elapsed}");

                if (!string.IsNullOrWhiteSpace(s.Goal))
                {
                    var goal = s.Goal.Length > 80 ? s.Goal[..80] + "..." : s.Goal;
                    node.AddNode($"[grey]Goal:[/] {Markup.Escape(goal)}");
                }
                if (!string.IsNullOrWhiteSpace(s.Result))
                {
                    var result = s.Result.Length > 80 ? s.Result[..80] + "..." : s.Result;
                    node.AddNode($"[grey]Result:[/] {Markup.Escape(result)}");
                }
            }
            if (sessions.Count == 0)
                tree.AddNode("[grey]No active sub-agents[/]");
        }
        else
        {
            tree.AddNode("[grey]Coordinator not connected[/]");
        }
        return tree;
    }

    private static string GetSpinner() => (DateTime.Now.Millisecond / 250 % 4) switch
    {
        0 => "◐", 1 => "◓", 2 => "◑", _ => "◒"
    };
}
