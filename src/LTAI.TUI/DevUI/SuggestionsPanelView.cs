// Copyright (c) LTAI. All rights reserved.

using LTAI.Agent.DevUI;
using LTAI.Agent.Suggestions;
using Spectre.Console;
using System.Text;

namespace LTAI.TUI.DevUI;

public sealed class SuggestionsPanelView
{
    private readonly LTAIDevUIService _devUi;
    private readonly Table _table;

    public SuggestionsPanelView(LTAIDevUIService devUi)
    {
        _devUi = devUi;
        _table = new Table().Border(TableBorder.Rounded);
        _table.AddColumn("Severity");
        _table.AddColumn("Category");
        _table.AddColumn("Issue");
        _table.AddColumn("File");
        _table.AddColumn("Suggestion");
    }

    public void Render()
    {
        AnsiConsole.Clear();
        var stats = _devUi.GetSuggestionStats();

        var header = new Panel(new Markup(
            $"[bold]Suggestions Dashboard[/]  [grey]·[/]  " +
            $"Total: [cyan]{stats.Total}[/]  " +
            $"Critical: [red bold]{stats.Critical}[/]  " +
            $"Warnings: [yellow]{stats.Warnings}[/]  " +
            $"Detectors: [cyan]{string.Join(", ", _devUi.GetDetectorNames())}[/]"))
        {
            Border = BoxBorder.None,
            Expand = true,
        };
        AnsiConsole.Write(header);

        if (stats.ByCategory.Count > 0)
        {
            var catBar = new BarChart()
                .Label("Issues by Category");
            foreach (var (cat, count) in stats.ByCategory.OrderByDescending(kv => kv.Value))
            {
                var color = cat switch
                {
                    "todo" => Color.Yellow,
                    "naming" => Color.Cyan,
                    "complexity" => Color.Red,
                    "magic" => Color.Blue,
                    "exception" => Color.Red3,
                    "documentation" => Color.Green,
                    _ => Color.Grey,
                };
                catBar.AddItem(cat, count, color);
            }
            AnsiConsole.Write(catBar);
        }

        var suggestions = _devUi.GetSuggestions();
        if (suggestions.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]✓ No issues found — workspace looks clean![/]\n");
        }
        else
        {
            _table.Rows.Clear();
            foreach (var issue in suggestions.Take(20))
            {
                var severity = issue.Severity switch
                {
                    IssueSeverity.Critical => $"[red bold]CRIT[/]",
                    IssueSeverity.Warning => $"[yellow]WARN[/]",
                    _ => $"[grey]INFO[/]",
                };
                _table.AddRow(
                    severity,
                    $"[cyan]{issue.Category}[/]",
                    $"[white]{issue.Title.EscapeMarkup()}[/]",
                    $"[grey]{issue.File}:{issue.Line}[/]",
                    issue.Suggestion != null ? $"[dim]{issue.Suggestion.EscapeMarkup()}[/]" : "-");
            }
            AnsiConsole.Write(_table);
        }

        AnsiConsole.MarkupLine("\n[dim]F5: Rescan  Q: Back[/]");
    }
}
