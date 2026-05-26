using System.Reflection;
using Spectre.Console;
using Spectre.Console.Rendering;
using LTAI.DNA;

namespace LTAI.TUI;

public sealed class DnaEvolutionView
{
    private readonly DNAOrchestrator? _dna;

    public DnaEvolutionView(DNAOrchestrator? dna = null)
    {
        _dna = dna;
    }

    public IRenderable Render()
    {
        if (_dna == null)
            return new Markup("[grey]DNA subsystem not available.[/]");

        var sections = new List<IRenderable>
        {
            BuildStatus(),
            new Rule("[green]Mutation Tree[/]").RuleStyle(Style.Plain),
            BuildEvolutionTree(),
            new Rule("[yellow]Safety Rules[/]").RuleStyle(Style.Plain),
            BuildSafetyPanel(),
            new Rule("[blue]Evolution Timeline[/]").RuleStyle(Style.Plain),
            BuildTimeline()
        };

        var panel = new Panel(new Rows(sections))
        {
            Header = new PanelHeader("[green]DNA Evolution[/]"),
            Border = BoxBorder.Rounded
        };
        return panel;
    }

    private IRenderable BuildStatus()
    {
        var status = _dna!.GetStatus();

        var grid = new Grid().AddColumns(3);

        grid.AddRow(
            new Panel(new Markup($"[cyan]Consciousness:[/] {status.ConsciousnessLevel}\n[cyan]Awareness:[/] {status.AwarenessScore:F3}\n[cyan]Thoughts:[/] {status.ActiveThoughts}"))
                .RoundedBorder(),

            new Panel(new Markup($"[green]Generation:[/] {status.Generation}\n[green]Fitness:[/] {status.FitnessScore:F3}\n[green]Energy:[/] {status.EnergyLevel:F2}"))
                .RoundedBorder(),

            new Panel(new Markup($"[yellow]Safety:[/] {status.SafetyPosture}\n[yellow]Habits:[/] {status.HabitCount}\n[yellow]Compiled:[/] {status.CompiledPathCount}"))
                .RoundedBorder()
        );

        return grid;
    }

    private IRenderable BuildEvolutionTree()
    {
        var tree = new Tree("[green]Evolution Rules[/]");

        try
        {
            var rules = _dna!.SelfEvo.Rules;
            if (rules.Count == 0)
            {
                tree.AddNode("[grey]No evolution rules yet[/]");
                return tree;
            }

            foreach (var (name, rule) in rules.OrderByDescending(r => r.Value.Strength))
            {
                var strengthBar = new string('█', (int)(rule.Strength * 20));
                var emptyBar = new string('░', 20 - (int)(rule.Strength * 20));
                var color = rule.Strength > 0.7 ? "green" : rule.Strength > 0.4 ? "yellow" : "red";
                tree.AddNode($"[{color}]{name}[/] [{color}]{strengthBar}{emptyBar}[/] [dim]{rule.Strength:F3}[/]");
            }
        }
        catch { tree.AddNode("[grey]Evolution rules unavailable[/]"); }

        return tree;
    }

    private IRenderable BuildSafetyPanel()
    {
        try
        {
            var safety = _dna!.Safety;
            var report = safety.GetStatus();

            var grid = new Grid().AddColumns(2);

            grid.AddRow(
                new Markup("[cyan]Posture:[/]"),
                new Markup(PostureColor(report.Posture.ToString()))
            );
            grid.AddRow(
                new Markup("[cyan]Known Threats:[/]"),
                new Markup($"[white]{report.KnownThreats}[/]")
            );
            grid.AddRow(
                new Markup("[cyan]Alignment:[/]"),
                new Markup($"[white]{report.AlignmentScore:F3}[/]")
            );

            var mutationRate = _dna!.SelfEvo.MutationRate;
            grid.AddRow(
                new Markup("[cyan]Mutation Rate:[/]"),
                new Markup($"[white]{mutationRate:F3}[/]")
            );

            return new Panel(grid).RoundedBorder();
        }
        catch
        {
            return new Markup("[grey]Safety info unavailable[/]");
        }
    }

    private IRenderable BuildTimeline()
    {
        var tree = new Tree("[blue]Evolution Timeline[/]");

        try
        {
            var historyField = typeof(SelfEvolution).GetField("_history", BindingFlags.NonPublic | BindingFlags.Instance);
            if (historyField == null)
            {
                tree.AddNode("[grey]No history access[/]");
                return tree;
            }

            var history = historyField.GetValue(_dna!.SelfEvo) as System.Collections.IList;
            if (history == null || history.Count == 0)
            {
                tree.AddNode("[grey]No evolution events recorded yet[/]");
                return tree;
            }

            var events = history.Cast<object>()
                .Reverse()
                .Take(20)
                .ToList();

            foreach (var evt in events)
            {
                var type = evt.GetType();
                var rule = type.GetProperty("Rule")?.GetValue(evt) ?? "?";
                var oldStr = type.GetProperty("OldStrength")?.GetValue(evt);
                var newStr = type.GetProperty("NewStrength")?.GetValue(evt);
                var trigger = type.GetProperty("Trigger")?.GetValue(evt) ?? "?";

                var delta = oldStr is double o && newStr is double n ? n - o : 0;
                var deltaSign = delta >= 0 ? "+" : "";
                var deltaColor = delta >= 0 ? "green" : "red";

                tree.AddNode($"[dim]{rule}[/] [{deltaColor}]{deltaSign}{delta:F3}[/] [grey]({trigger})[/]");
            }
        }
        catch { tree.AddNode("[grey]Timeline unavailable[/]"); }

        return tree;
    }

    private static string PostureColor(string posture) => posture switch
    {
        "Permissive" => "[red]Permissive[/]",
        "Cautious" => "[green]Cautious[/]",
        "Guarded" => "[yellow]Guarded[/]",
        "Defensive" => "[red]Defensive[/]",
        "Lockdown" => "[red]Lockdown[/]",
        _ => $"[white]{posture}[/]"
    };
}
