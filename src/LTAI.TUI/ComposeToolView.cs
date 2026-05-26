using Spectre.Console;
using Spectre.Console.Rendering;
using LTAI.Agent.Tools;
using LTAI.Models;

namespace LTAI.TUI;

public sealed class ComposeToolView
{
    private List<MkTool> _composeTools = new();
    private bool _loaded;

    private const string SeqIcon = "\u25b6";
    private const string ParIcon = "\u23e9";

    public void LoadTools(List<MkTool> tools)
    {
        _composeTools = tools.Where(t => t.Type == MkToolType.Compose).ToList();
        _loaded = true;
    }

    public async Task LoadFromDirectoryAsync(string toolsDir, CancellationToken ct = default)
    {
        if (!Directory.Exists(toolsDir)) return;

        var loader = new ToolLoader(Microsoft.Extensions.Logging.Abstractions.NullLogger<ToolLoader>.Instance);
        var files = Directory.GetFiles(toolsDir, "*.md", SearchOption.AllDirectories);

        var tools = new List<MkTool>();
        foreach (var file in files)
        {
            var tool = await loader.LoadAsync(file, ct).ConfigureAwait(false);
            if (tool != null)
                tools.Add(tool);
        }

        LoadTools(tools);
    }

    public void LoadFromToolService(ToolService service)
    {
        LoadTools(service.AllTools.ToList());
    }

    public IRenderable RenderTool(MkTool tool)
    {
        var panel = new Panel(BuildToolDetail(tool))
        {
            Header = new PanelHeader($"[cyan bold]{tool.Name}[/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Cyan1),
            Padding = new Padding(1, 1)
        };
        return panel;
    }

    private IRenderable BuildToolDetail(MkTool tool)
    {
        var grid = new Grid().AddColumns(2);

        grid.AddRow(
            new Markup($"[grey]Domain:[/] [yellow]{tool.Domain}[/]"),
            new Markup($"[grey]Type:[/] [green]{tool.Type}[/]"));

        if (!string.IsNullOrEmpty(tool.Description))
            grid.AddRow(
                new Markup($"[grey]Description:[/] [white]{EscapeM(tool.Description)}[/]"),
                new Markup(""));

        if (tool.Triggers.Count > 0)
            grid.AddRow(
                new Markup($"[grey]Triggers:[/] [yellow]{string.Join(", ", tool.Triggers.Select(t => t.Pattern).Take(5))}[/]"),
                new Markup(""));

        if (tool.Tags.Count > 0)
            grid.AddRow(
                new Markup($"[grey]Tags:[/] [cyan]{string.Join(", ", tool.Tags)}[/]"),
                new Markup(""));

        if (tool.Steps.Count == 0)
        {
            grid.AddRow(new Markup("[grey]No steps defined[/]"), new Markup(""));
            return grid;
        }

        var stepsPanel = new Panel(BuildStepsTree(tool))
        {
            Header = new PanelHeader("[yellow]Steps[/]"),
            Border = BoxBorder.Rounded,
            Padding = new Padding(1, 0)
        };

        var container = new Grid();
        container.AddRow(grid);
        container.AddRow(stepsPanel);

        return container;
    }

    private IRenderable BuildStepsTree(MkTool tool)
    {
        var tree = new Tree("[yellow]Flow[/]");

        for (int i = 0; i < tool.Steps.Count; i++)
        {
            var step = tool.Steps[i];
            var isParallel = step.Parallel;
            var icon = isParallel ? ParIcon : SeqIcon;
            var color = isParallel ? "yellow" : "green";

            var label = $"[{color}]{icon} {step.Name}[/]";
            if (step.ToolRef != null)
                label += $" [grey][{step.ToolRef}][/]";

            var node = tree.AddNode(label);

            foreach (var kv in step.Inputs)
            {
                var valColor = kv.Value.Contains("{{") ? "magenta" : "grey";
                node.AddNode($"[grey]($[/][white]{kv.Key}[/]: [{valColor}]{kv.Value}[/][grey])[/]");
            }

            if (i < tool.Steps.Count - 1 && !step.Parallel)
            {
                node.AddNode("[dim]\u2193[/]");
            }
        }

        return tree;
    }

    public IRenderable RenderFlowChart(MkTool tool)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Cyan1)
            .AddColumn("[grey]#[/]")
            .AddColumn("[cyan]Step[/]")
            .AddColumn("[yellow]Type[/]")
            .AddColumn("[grey]Command[/]")
            .AddColumn("[grey]Inputs[/]")
            .AddColumn("[grey]Deps[/]");

        for (int i = 0; i < tool.Steps.Count; i++)
        {
            var step = tool.Steps[i];
            var isParallel = step.Parallel;
            var icon = isParallel ? ParIcon : SeqIcon;
            var typeColor = isParallel ? "yellow" : "green";
            var typeLabel = isParallel ? "parallel" : "sequential";

            var inputs = step.Inputs.Count > 0
                ? string.Join(" ", step.Inputs.Select(kv => $"({kv.Key}:{EscapeM(kv.Value)})"))
                : "—";

            var deps = new List<string>();
            foreach (var iv in step.Inputs.Values)
            {
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(iv, @"\$(\w+)"))
                {
                    var depName = m.Value.TrimStart('$');
                    if (tool.Steps.Any(s => s.Name == depName))
                        deps.Add(depName);
                }
            }
            var depStr = deps.Count > 0 ? string.Join(", ", deps.Select(d => $"\u2190 {d}")) : "—";

            table.AddRow(
                new Markup($"[grey]{i + 1}[/]"),
                new Markup($"[white]{icon} {step.Name}[/]"),
                new Markup($"[{typeColor}]{typeLabel}[/]"),
                new Markup(step.ToolRef != null ? $"[grey][{step.ToolRef}][/]" : "[dim]inline[/]"),
                new Markup($"[grey]{inputs}[/]"),
                new Markup(depStr));
        }

        var legend = new Panel(
            new Markup($"[green]{SeqIcon} Sequential[/]   [yellow]{ParIcon} Parallel[/]   [magenta]{{{{var}}}} Placeholder[/]   [grey]\u2190 Depends on[/]"))
        {
            Header = new PanelHeader("[cyan]Legend[/]"),
            Border = BoxBorder.Rounded,
            Padding = new Padding(1, 0)
        };

        var flowPanel = new Panel(table)
        {
            Header = new PanelHeader($"[cyan bold]Flowchart: {tool.Name}[/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Cyan1),
            Padding = new Padding(1, 1)
        };

        var container = new Grid();
        container.AddRow(flowPanel);
        container.AddRow(legend);

        return container;
    }

    public IRenderable RenderAllComposeTools()
    {
        if (!_loaded || _composeTools.Count == 0)
        {
            LoadToolsFromDirectorySync();
        }

        if (_composeTools.Count == 0)
            return new Panel(new Markup("[grey]No compose tools found. Place .md tool files in the tools/ directory.[/]"))
            {
                Header = new PanelHeader("[yellow]Compose Tools[/]"),
                Border = BoxBorder.Rounded
            };

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Cyan1)
            .Title("[cyan bold]Compose Tools[/]")
            .AddColumn("[grey]#[/]")
            .AddColumn("[cyan]Name[/]")
            .AddColumn("[yellow]Domain[/]")
            .AddColumn("[green]Steps[/]")
            .AddColumn("[grey]Description[/]");

        for (int i = 0; i < _composeTools.Count; i++)
        {
            var tool = _composeTools[i];
            table.AddRow(
                new Markup($"[grey]{i + 1}[/]"),
                new Markup($"[white]{tool.Name}[/]"),
                new Markup($"[yellow]{tool.Domain}[/]"),
                new Markup(tool.Steps.Count > 0 ? $"[green]{tool.Steps.Count}[/]" : "[dim]0[/]"),
                new Markup($"[grey]{EscapeM(tool.Description.Length > 60 ? tool.Description[..57] + "..." : tool.Description)}[/]"));
        }

        var hint = new Markup($"[grey]Press[/] [yellow]1-{Math.Min(_composeTools.Count, 9)}[/] [grey]to view tool detail  |  [/] [yellow]f[/] [grey] flowchart  |  [/] [yellow]Esc[/] [grey] back[/]");

        var container = new Grid();
        container.AddRow(table);
        container.AddRow(new Padder(hint, new Padding(0, 1)));

        return container;
    }

    public MkTool? GetTool(int index)
    {
        if (index >= 0 && index < _composeTools.Count)
            return _composeTools[index];
        return null;
    }

    public int ToolCount => _composeTools.Count;

    private void LoadToolsFromDirectorySync()
    {
        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "tools"),
            Path.Combine(AppContext.BaseDirectory, "tools")
        };

        foreach (var dir in candidates)
        {
            if (Directory.Exists(dir))
            {
                var loader = new ToolLoader(Microsoft.Extensions.Logging.Abstractions.NullLogger<ToolLoader>.Instance);
                var files = Directory.GetFiles(dir, "*.md", SearchOption.AllDirectories);

                var tools = new List<MkTool>();
                foreach (var file in files)
                {
                    try
                    {
                        var text = File.ReadAllText(file);
                        var tool = loader.Parse(file, text);
                        if (tool != null)
                            tools.Add(tool);
                    }
                    catch
                    {
                    }
                }

                LoadTools(tools);
                return;
            }
        }
    }

    private static string EscapeM(string text) =>
        text.Replace("[", "[[").Replace("]", "]]");
}
