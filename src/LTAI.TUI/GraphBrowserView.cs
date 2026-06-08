using System.Text;
using Spectre.Console;
using LTAI.Agent.Vector;
using LTAI.Agent.Formats;

namespace LTAI.TUI;

public sealed class GraphBrowserView
{
    private readonly KbGraph? _kg;
    private readonly KgStore? _store;

    public GraphBrowserView(KbGraph? kg = null, KgStore? store = null)
    {
        _kg = kg;
        _store = store;
    }

    public bool Available => _kg != null || _store != null;

    public void Render()
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule("[bold yellow]Knowledge Graph Browser[/]"));
        AnsiConsole.MarkupLine("[dim]Commands: [cyan]search <query>[/]  [cyan]stats[/]  [cyan]kind <type>[/]  [cyan]node <id>[/]  [cyan]q[/]=return[/]\n");

        if (!Available)
        {
            AnsiConsole.MarkupLine("[red]No knowledge graph available. Ensure KbGraph is registered.[/]");
            AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title("[dim]Press Enter to return[/]")
                .PageSize(3)
                .AddChoices("Return"));
            return;
        }

        if (_store != null)
            RenderStatsSummary();

        bool running = true;
        while (running)
        {
            var input = AnsiConsole.Prompt(
                new TextPrompt<string>("[bold yellow]graph>[/]")
                    .PromptStyle("cyan")
                    .AllowEmpty());

            if (string.IsNullOrWhiteSpace(input))
                continue;

            var parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var cmd = parts[0].ToLowerInvariant();

            switch (cmd)
            {
                case "q":
                case "quit":
                case "exit":
                case "return":
                    running = false;
                    break;

                case "stats":
                    RenderStatsPanel();
                    break;

                case "search":
                    HandleSearch(parts);
                    break;

                case "kind":
                    HandleKind(parts);
                    break;

                case "node":
                    HandleNodeDetail(parts);
                    break;

                case "ls":
                    HandleList(parts);
                    break;

                case "help":
                    AnsiConsole.MarkupLine("[cyan]search <query>[/]  — Search knowledge graph");
                    AnsiConsole.MarkupLine("[cyan]stats[/]          — Show graph statistics");
                    AnsiConsole.MarkupLine("[cyan]kind <type>[/]    — List entities by kind");
                    AnsiConsole.MarkupLine("[cyan]node <id>[/]      — Show node details + connections");
                    AnsiConsole.MarkupLine("[cyan]ls[/]             — List all node kinds with counts");
                    AnsiConsole.MarkupLine("[cyan]q[/]              — Return to chat");
                    break;

                default:
                    AnsiConsole.MarkupLine($"[red]Unknown command: {Markup.Escape(cmd)}[/]");
                    AnsiConsole.MarkupLine("[dim]Try: search, stats, kind, node, ls, help, q[/]");
                    break;
            }
        }
    }

    private void RenderStatsSummary()
    {
        if (_store == null) return;
        try
        {
            var stats = _store.Stats().GetAwaiter().GetResult();
            var panel = new Panel(new Markup($"[green]{Markup.Escape(stats)}[/]"))
            {
                Border = BoxBorder.Rounded,
                Header = new PanelHeader("[bold]Graph Stats[/]"),
                Expand = true,
            };
            AnsiConsole.Write(panel);
        }
        catch
        {
        }
    }

    private void RenderStatsPanel()
    {
        if (_store == null)
        {
            AnsiConsole.MarkupLine("[yellow]Stats require KgStore access.[/]");
            return;
        }

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("[yellow]Loading stats...[/]", async _ =>
            {
                try
                {
                    var stats = await _store.Stats().ConfigureAwait(false);
                    var panel = new Panel(new Markup($"[green]{Markup.Escape(stats)}[/]"))
                    {
                        Border = BoxBorder.Rounded,
                        Header = new PanelHeader("[bold]Full Graph Statistics[/]"),
                        Expand = true,
                    };
                    AnsiConsole.Write(panel);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
                }
            });
    }

    private void HandleSearch(string[] parts)
    {
        if (_kg == null)
        {
            AnsiConsole.MarkupLine("[yellow]Search requires KbGraph service.[/]");
            return;
        }

        var query = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : "";
        if (string.IsNullOrWhiteSpace(query))
        {
            AnsiConsole.MarkupLine("[red]Usage: search <query>[/]");
            return;
        }

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start($"[yellow]Searching for '{Markup.Escape(query)}'...[/]", async _ =>
            {
                try
                {
                    var results = await _kg.QueryAsync(query, topK: 10,
                        format: ResultFormat.Markdown).ConfigureAwait(false);

                    if (results.Count == 0)
                    {
                        AnsiConsole.MarkupLine("[yellow]No results found.[/]");
                        return;
                    }

                    AnsiConsole.Write(new Rule($"[bold cyan]Query: '{Markup.Escape(query)}'[/]"));
                    foreach (var section in results)
                    {
                        var panel = new Panel(new Markup(Markup.Escape(section)))
                        {
                            Border = BoxBorder.Heavy,
                            Expand = true,
                            Padding = new Padding(2, 1),
                        };
                        AnsiConsole.Write(panel);
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Search error: {Markup.Escape(ex.Message)}[/]");
                }
            });
    }

    private void HandleKind(string[] parts)
    {
        if (_store == null)
        {
            AnsiConsole.MarkupLine("[yellow]Kind listing requires store access.[/]");
            return;
        }

        var kind = parts.Length > 1 ? parts[1] : "";
        if (string.IsNullOrWhiteSpace(kind))
        {
            AnsiConsole.MarkupLine("[yellow]Usage: kind <type>[/]");
            AnsiConsole.MarkupLine("[dim]Use [cyan]ls[/] to list available kinds with counts.[/]");
            return;
        }

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start($"[yellow]Loading {Markup.Escape(kind)} nodes...[/]", async _ =>
            {
                try
                {
                    var nodes = await _store.GetNodesByKind(kind).ConfigureAwait(false);
                    if (nodes.Count == 0)
                    {
                        AnsiConsole.MarkupLine(
                            $"[yellow]No nodes of kind '{Markup.Escape(kind)}' found.[/]");
                        return;
                    }

                    var table = new Table().Border(TableBorder.Rounded)
                        .Title($"[bold]{KindIcon(kind)} {Markup.Escape(kind)} ({nodes.Count})[/]");
                    table.AddColumn("ID");
                    table.AddColumn("Name");
                    table.AddColumn("Namespace");
                    table.AddColumn("Source");

                    foreach (var node in nodes.Take(30))
                    {
                        table.AddRow(
                            node.Id.ToString(),
                            Markup.Escape(Truncate(node.Name, 40)),
                            Markup.Escape(Truncate(node.Namespace ?? "-", 25)),
                            Markup.Escape(Truncate(node.Source ?? "-", 30)));
                    }

                    AnsiConsole.Write(table);
                    if (nodes.Count > 30)
                        AnsiConsole.MarkupLine($"[dim]... and {nodes.Count - 30} more entries[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
                }
            });
    }

    private void HandleList(string[] parts)
    {
        if (_store == null)
        {
            AnsiConsole.MarkupLine("[yellow]Listing requires store access.[/]");
            return;
        }

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("[yellow]Loading node kinds...[/]", async _ =>
            {
                try
                {
                    var allNodes = await _store.GetAllNodes().ConfigureAwait(false);
                    var groups = allNodes
                        .GroupBy(n => n.Kind)
                        .OrderByDescending(g => g.Count())
                        .ToList();

                    var total = allNodes.Count;

                    var table = new Table().Border(TableBorder.Rounded)
                        .Title($"[bold]Node Kinds ({total} total)[/]");
                    table.AddColumn("Kind");
                    table.AddColumn("Count");
                    table.AddColumn("");

                    foreach (var group in groups)
                    {
                        var bar = new string('█', Math.Clamp(group.Count() * 20 / Math.Max(groups[0].Count(), 1), 1, 20));
                        table.AddRow(
                            $"{KindIcon(group.Key)} {Markup.Escape(group.Key)}",
                            group.Count().ToString(),
                            $"[green]{bar}[/]");
                    }

                    AnsiConsole.Write(table);

                    var edges = await _store.GetEdges(null).ConfigureAwait(false);
                    AnsiConsole.MarkupLine($"[dim]Total edges: {edges.Count}[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
                }
            });
    }

    private void HandleNodeDetail(string[] parts)
    {
        if (_store == null)
        {
            AnsiConsole.MarkupLine("[yellow]Node detail requires store access.[/]");
            return;
        }

        if (parts.Length < 2 || !long.TryParse(parts[1], out var nodeId))
        {
            AnsiConsole.MarkupLine("[red]Usage: node <id>[/]");
            return;
        }

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start($"[yellow]Loading node {nodeId}...[/]", async _ =>
            {
                try
                {
                    var node = await _store.GetNode(nodeId).ConfigureAwait(false);
                    if (node == null)
                    {
                        AnsiConsole.MarkupLine($"[red]Node {nodeId} not found.[/]");
                        return;
                    }

                    var info = new StringBuilder();
                    info.AppendLine($"{KindIcon(node.Kind)} [bold]{Markup.Escape(node.Name)}[/]  [dim]({Markup.Escape(node.Kind)})[/]");
                    info.AppendLine();
                    info.AppendLine($"[bold]ID:[/]        {node.Id}");
                    if (!string.IsNullOrEmpty(node.ExtId))
                        info.AppendLine($"[bold]Ext ID:[/]    {Markup.Escape(node.ExtId)}");
                    if (!string.IsNullOrEmpty(node.Namespace))
                        info.AppendLine($"[bold]Namespace:[/] {Markup.Escape(node.Namespace)}");
                    if (!string.IsNullOrEmpty(node.Source))
                        info.AppendLine($"[bold]Source:[/]    {Markup.Escape(node.Source)}");
                    if (!string.IsNullOrEmpty(node.Signature))
                        info.AppendLine($"[bold]Signature:[/] {Markup.Escape(node.Signature)}");
                    info.AppendLine($"[bold]Created:[/]   {node.CreatedAt}");
                    info.AppendLine($"[bold]Updated:[/]   {node.UpdatedAt}");

                    var infoPanel = new Panel(new Markup(info.ToString()))
                    {
                        Border = BoxBorder.Rounded,
                        Header = new PanelHeader($"[bold]Node {nodeId}[/]"),
                        Expand = true,
                    };
                    AnsiConsole.Write(infoPanel);

                    await RenderNodeGraphAsync(node).ConfigureAwait(false);
                    await RenderNodeDocsAsync(node).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
                }
            });
    }

    private async Task RenderNodeGraphAsync(NodeRow node)
    {
        if (_store == null) return;

        var edges = await _store.GetEdges(node.Id).ConfigureAwait(false);
        if (edges.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No connections.[/]");
            return;
        }

        AnsiConsole.Write(new Rule($"[bold cyan]Neighbors ({edges.Count})[/]"));

        var root = new Tree($"{KindIcon(node.Kind)} [bold]{Markup.Escape(node.Name)}[/]");

        foreach (var edge in edges.OrderByDescending(e => e.Weight).Take(12))
        {
            var neighborId = edge.Src == node.Id ? edge.Dst : edge.Src;
            var neighbor = await _store.GetNode(neighborId).ConfigureAwait(false);
            var neighborLabel = neighbor != null
                ? $"{KindIcon(neighbor.Kind)} [cyan]{Markup.Escape(Truncate(neighbor.Name, 40))}[/] [dim]({neighbor.Kind})[/]"
                : $"[grey]Node {neighborId}[/]";
            var direction = edge.Src == node.Id ? "[bold]→[/]" : "[bold]←[/]";
            var weight = edge.Weight != 1.0 ? $" [dim]w={edge.Weight:F1}[/]" : "";

            root.AddNode($"[yellow]{Markup.Escape(edge.Relation)}[/] {direction} {neighborLabel}{weight}");
        }

        if (edges.Count > 12)
            root.AddNode($"[dim]... and {edges.Count - 12} more[/]");

        AnsiConsole.Write(root);
    }

    private async Task RenderNodeDocsAsync(NodeRow node)
    {
        if (_store == null) return;

        var docs = await _store.GetDocs(node.Id).ConfigureAwait(false);
        if (docs.Count == 0) return;

        AnsiConsole.Write(new Rule($"[bold]Text Units ({docs.Count})[/]"));

        foreach (var doc in docs.Take(3))
        {
            var snippet = doc.Text.Length > 250
                ? doc.Text[..250] + "..."
                : doc.Text;
            var panel = new Panel(new Markup($"[grey]{Markup.Escape(snippet)}[/]"))
            {
                Border = BoxBorder.None,
                Expand = true,
                Padding = new Padding(2, 0),
            };
            AnsiConsole.Write(panel);
        }
        if (docs.Count > 3)
            AnsiConsole.MarkupLine($"[dim]... and {docs.Count - 3} more text units[/]");
    }

    private static string KindIcon(string kind) => kind.ToLowerInvariant() switch
    {
        "document" => "📄",
        "concept" => "🏷️",
        "fact" => "💡",
        "class" => "🔷",
        "method" or "function" => "⚙️",
        "interface" => "🔲",
        "enum" => "🔢",
        "struct" => "🏗️",
        "file" => "📁",
        "wiki" => "📝",
        "note" => "📋",
        "chunk" => "🧩",
        "record" => "📀",
        "property" or "field" => "🔹",
        "module" or "namespace" => "📦",
        _ => "▪️",
    };

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}
