// Copyright (c) LTAI. All rights reserved.

using LTAI.Agent.DevUI;
using LTAI.Core.Configuration;
using Spectre.Console;
using LTAI.AI;

namespace LTAI.TUI.DevUI;

/// <summary>
/// Three-panel TUI dashboard backed by <see cref="LTAIDevUIService"/> (agent
/// enumeration + AgentCard) and <see cref="DevUISpanCollector"/> (live OTel
/// spans). Bound to <see cref="TuiView.Dashboard"/> in <c>TuiApp.ShowDashboard</c>.
/// </summary>
public static class DevUIDashboardView
{
    public static void Render(LTAIDevUIService devUi, DevUISpanCollector spans, UsageTracker? usage)
    {
        var cards = devUi.ListAgentCards();
        var recent = spans.Snapshot().TakeLast(15).Reverse().ToList();

        var layout = new Layout("root")
            .SplitRows(
                new Layout("header").Size(3),
                new Layout("body").SplitColumns(
                    new Layout("agents").Ratio(2),
                    new Layout("spans").Ratio(3)),
                new Layout("footer").Size(5));

        layout["header"].Update(
            new Panel(
                new Markup(
                    $"[bold]LTAI DevUI Dashboard[/]  [grey]·[/]  " +
                    $"[aqua]{cards.Count}[/] agents  [grey]·[/]  " +
                    $"[aqua]{spans.Count}[/] spans  [grey]·[/]  " +
                    $"[aqua]{recent.Count(s => s.IsLive)}[/] live"))
            {
                Border = BoxBorder.Heavy,
                Header = new PanelHeader("[green] P9 Live Inspector [/]"),
                Expand = true,
            });

        layout["agents"].Update(BuildAgentTable(cards));
        layout["spans"].Update(BuildSpanTable(recent));
        layout["footer"].Update(BuildUsagePanel(usage));

        AnsiConsole.Write(layout);
    }

    private static Panel BuildAgentTable(IReadOnlyList<LTAIAgentCard> cards)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey35)
            .Title("[bold yellow] Agents (LTAIAgentCard) [/]")
            .Expand();
        table.AddColumn(new TableColumn("[bold]Name[/]").NoWrap());
        table.AddColumn(new TableColumn("[bold]Model[/]"));
        table.AddColumn(new TableColumn("[bold]T[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Tools[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Perms[/]").NoWrap());
        table.AddColumn(new TableColumn("[bold]Description[/]"));

        foreach (var c in cards.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            var perms = c.Permissions.Count == 0
                ? "[grey]—[/]"
                : string.Join(" ", c.Permissions.Select(ColorizePerm));
            table.AddRow(
                $"[bold]{Markup.Escape(c.Name)}[/]",
                Markup.Escape(c.ModelId ?? "—"),
                c.Temperature.ToString("F1"),
                c.ToolCount.ToString(),
                perms,
                Markup.Escape(Truncate(c.Description, 40)));
        }
        return new Panel(table) { Expand = true };
    }

    private static Panel BuildSpanTable(IReadOnlyList<DevUISpan> recent)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey35)
            .Title("[bold cyan] OpenTelemetry Spans (live tail) [/]")
            .Expand();
        table.AddColumn(new TableColumn("[bold]Status[/]").NoWrap());
        table.AddColumn(new TableColumn("[bold]Name[/]"));
        table.AddColumn(new TableColumn("[bold]Source[/]"));
        table.AddColumn(new TableColumn("[bold]Kind[/]").NoWrap());
        table.AddColumn(new TableColumn("[bold]Duration[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Trace[/]").NoWrap());

        if (recent.Count == 0)
        {
            table.AddRow("[grey]·[/]", "[grey](no spans yet — start a chat to see traces)[/]", "", "", "", "");
        }
        else
        {
            foreach (var s in recent)
            {
                var statusTag = s.IsLive
                    ? "[yellow]● live[/]"
                    : s.Status == "ERROR" ? "[red]✖ ERR[/]"
                    : "[green]✓ OK [/]";
                var duration = s.IsLive ? "[yellow]...[/]" : FormatMs(s.Duration);
                var color = s.IsLive ? "yellow" :
                            s.Duration > TimeSpan.FromSeconds(2) ? "red" :
                            s.Duration > TimeSpan.FromMilliseconds(500) ? "yellow" : "grey";
                table.AddRow(
                    statusTag,
                    $"[{color}]{Markup.Escape(Truncate(s.Name, 36))}[/]",
                    Markup.Escape(Truncate(s.Source, 28)),
                    s.Kind,
                    duration,
                    Markup.Escape(s.TraceId.Length >= 8 ? s.TraceId[..8] : s.TraceId));
            }
        }
        return new Panel(table) { Expand = true };
    }

    private static Panel BuildUsagePanel(UsageTracker? usage)
    {
        if (usage is null)
        {
            return new Panel(new Markup("[grey](usage tracker not available)[/]"))
            {
                Border = BoxBorder.Rounded,
                Header = new PanelHeader("[green] Token Usage [/]"),
                Expand = true,
            };
        }
        var grid = new Grid()
            .AddColumn(new GridColumn().RightAligned())
            .AddColumn()
            .AddColumn(new GridColumn().RightAligned())
            .AddColumn();
        grid.AddRow(
            "[bold]In[/]", $"{UsageTracker.PromptTokens:N0}",
            "[bold]Out[/]", $"{UsageTracker.CompletionTokens:N0}");
        grid.AddRow(
            "[bold]Total[/]", $"{UsageTracker.TotalTokens:N0}",
            "[bold]Requests[/]", $"{UsageTracker.Requests:N0}");
        grid.AddRow(
            "[bold]Cost[/]", UsageTracker.CostDisplay,
            "[bold]Uptime[/]", UsageTracker.Uptime.ToString(@"hh\:mm\:ss"));
        return new Panel(grid)
        {
            Border = BoxBorder.Rounded,
            Header = new PanelHeader("[green] Token Usage (this session) [/]"),
            Expand = true,
        };
    }

    private static string ColorizePerm(string perm)
    {
        return perm.ToLowerInvariant() switch
        {
            "read" => "[green]R[/]",
            "write" => "[yellow]W[/]",
            "list" => "[blue]L[/]",
            "exec" => "[red]X[/]",
            _ => $"[grey]{Markup.Escape(perm)}[/]",
        };
    }

    private static string FormatMs(TimeSpan d) => d.TotalMilliseconds switch
    {
        < 1 => $"{d.TotalMilliseconds:F2} ms",
        < 1000 => $"{d.TotalMilliseconds:F0} ms",
        < 60_000 => $"{d.TotalSeconds:F2} s",
        _ => $"{d.TotalMinutes:F1} m",
    };

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}
