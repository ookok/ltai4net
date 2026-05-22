using Spectre.Console.Rendering;
using System.Text;
using Spectre.Console;

namespace LTAI.TUI;

public sealed class TaskDagView
{
    public IRenderable Render(List<TaskEntry> tasks, string rootTask = "user_request")
    {
        if (tasks.Count == 0)
            return new Markup("[grey](No tasks)[/]");

        var tree = new Tree($"[cyan]{rootTask}[/]")
            .Style(Style.Parse("cyan"));

        var roots = tasks.Where(t => !tasks.Any(o => o.Name == t.Name && o != t)).ToList();
        var visited = new HashSet<string>();

        foreach (var task in tasks)
        {
            if (!visited.Add(task.Name)) continue;

            var depth = EstimateDepth(task, tasks);
            var indent = depth > 0 ? new string(' ', depth * 2) : "";
            var icon = task.Status switch
            {
                "done" => "[green]✓[/]",
                "running" => "[cyan]●[/]",
                "failed" => "[red]✗[/]",
                _ => "[grey]○[/]"
            };

            var elapsed = task.CompletedAt != null
                ? $"{task.CompletedAt.Value - task.StartedAt:mm\\:ss}"
                : $"{DateTime.Now - task.StartedAt:mm\\:ss}";

            tree.AddNode($"{icon} [white]{task.Name}[/] [grey]{elapsed}[/]");
        }

        return tree;
    }

    private static int EstimateDepth(TaskEntry task, List<TaskEntry> all)
    {
        return Math.Min(all.IndexOf(task) % 4, 3);
    }

    public IRenderable RenderFlowChart(List<TaskEntry> tasks)
    {
        if (tasks.Count == 0) return new Text("");

        var sb = new StringBuilder();
        sb.AppendLine("[cyan]Task Flow:[/]");
        sb.AppendLine();

        var phases = new[] { "input", "reasoning", "execution", "output" };
        var phaseTasks = phases.Select(p => tasks.Where(t => t.Name.Contains(p, StringComparison.OrdinalIgnoreCase)).ToList()).ToList();

        var maxHeight = phaseTasks.Max(p => p.Count);
        for (var row = 0; row < maxHeight; row++)
        {
            var cells = new List<string>();
            for (var col = 0; col < phases.Length; col++)
            {
                if (row < phaseTasks[col].Count)
                {
                    var task = phaseTasks[col][row];
                    var icon = task.Status == "done" ? "[green]✓[/]" :
                               task.Status == "running" ? "[cyan]●[/]" :
                               task.Status == "failed" ? "[red]✗[/]" : "[grey]○[/]";
                    cells.Add($"{icon} {task.Name[..Math.Min(task.Name.Length, 12)]}");
                }
                else
                {
                    cells.Add(row == 0 ? $"[grey]{phases[col]}[/]" : "    ");
                }
            }
            sb.AppendLine(string.Join(" [grey]→[/] ", cells));
        }

        return new Panel(new Markup(sb.ToString().TrimEnd()))
        {
            Header = new PanelHeader("[cyan]DAG Flow[/]"),
            Border = BoxBorder.Rounded
        };
    }
}
