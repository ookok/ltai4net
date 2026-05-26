using LTAI.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LTAI.TUI;

public sealed class FunnelView
{
    private readonly List<RequestTrace> _traces = new();

    public void RecordRequest(string query)
    {
        _traces.Add(new RequestTrace { Query = query, StartTime = DateTime.UtcNow });
        if (_traces.Count > 20) _traces.RemoveAt(0);
    }

    public void RecordStage(string stage, string? detail = null)
    {
        if (_traces.Count == 0) return;
        var trace = _traces[^1];
        var now = DateTime.UtcNow;
        var lastTime = trace.LastStageTime ?? trace.StartTime;
        trace.Stages.Add(new StageEntry(stage, detail, (now - lastTime).TotalMilliseconds));
        trace.LastStageTime = now;
    }

    public void RecordToolCall(string toolName, double durationMs, string status = "success")
    {
        if (_traces.Count == 0) return;
        _traces[^1].ToolCalls.Add(new ToolCallEntry(toolName, durationMs, status));
    }

    public void SetModelInfo(string layer, string model, string? confidence = null)
    {
        if (_traces.Count == 0) return;
        var trace = _traces[^1];
        trace.ModelLayer = layer;
        trace.ModelName = model;
        trace.RouteConfidence = confidence;
    }

    public void SetTokenUsage(int inputTokens, int outputTokens)
    {
        if (_traces.Count == 0) return;
        var trace = _traces[^1];
        trace.InputTokens = inputTokens;
        trace.OutputTokens = outputTokens;
    }

    public void Complete(string response, int inputTokens = 0, int outputTokens = 0)
    {
        if (_traces.Count == 0) return;
        var trace = _traces[^1];
        trace.Response = response;
        trace.EndTime = DateTime.UtcNow;
        if (inputTokens > 0) trace.InputTokens = inputTokens;
        if (outputTokens > 0) trace.OutputTokens = outputTokens;
    }

    public IRenderable Render(LTAIOptions? options = null)
    {
        if (_traces.Count == 0)
            return new Markup("[grey]No requests yet. Send a chat message first.[/]");

        var trace = _traces[^1];
        var totalMs = trace.EndTime.HasValue
            ? (trace.EndTime.Value - trace.StartTime).TotalMilliseconds
            : (DateTime.UtcNow - trace.StartTime).TotalMilliseconds;

        var parts = new List<IRenderable>();

        parts.Add(new Markup($"[dim]Query: {Markup.Escape(trace.Query[..Math.Min(trace.Query.Length, 80)])}[/]"));

        var table = new Table().Expand().Border(TableBorder.Rounded);
        table.AddColumn(new TableColumn("[cyan]Stage[/]"));
        table.AddColumn(new TableColumn("[yellow]Detail[/]"));
        table.AddColumn(new TableColumn("[green]Time (ms)[/]").RightAligned());
        table.AddColumn(new TableColumn("[blue]%[/]").RightAligned());

        foreach (var stage in trace.Stages)
        {
            var ms = stage.Ms;
            var pct = totalMs > 0 ? (ms / totalMs * 100) : 0;
            var bar = new string('\u2588', Math.Max(1, Math.Min(40, (int)(pct / 2.5))));
            var emptyBar = new string('\u2591', Math.Max(0, 40 - Math.Min(40, (int)(pct / 2.5))));
            table.AddRow(
                new Markup($"[white]{Markup.Escape(stage.Stage)}[/]"),
                new Markup($"[grey]{Markup.Escape(stage.Detail ?? "")}[/]"),
                new Markup($"[green]{ms:F0}[/]"),
                new Markup($"[blue]{bar}[/][dim]{emptyBar}[/] [bold]{pct:F0}%[/]"));
        }

        var totalTokens = trace.InputTokens + trace.OutputTokens;
        table.AddRow(
            new Markup("[bold white]Total[/]"),
            new Markup($"[grey]{(totalTokens > 0 ? $"{totalTokens} tokens" : "")}[/]"),
            new Markup($"[bold green]{totalMs:F0}[/]"),
            new Markup("[bold]100%[/]"));

        parts.Add(table);
        parts.Add(new Text(""));

        var modelDisplay = trace.ModelLayer != null
            ? $"{Markup.Escape(trace.ModelLayer)}:{Markup.Escape(trace.ModelName ?? "?")}"
            : Markup.Escape(trace.ModelName ?? "?");
        var confDisplay = trace.RouteConfidence != null ? $" (conf={Markup.Escape(trace.RouteConfidence)})" : "";

        var tokenPanel = new Panel($$"""
            [cyan]Input:[/] {{trace.InputTokens:N0}} tokens
            [cyan]Output:[/] {{trace.OutputTokens:N0}} tokens
            [cyan]Total:[/] {{(trace.InputTokens + trace.OutputTokens):N0}} tokens
            [cyan]Model:[/] {{modelDisplay}}{{confDisplay}}
            """)
            .RoundedBorder().Header("Token Usage").BorderColor(Color.Cyan1).Padding(1, 1);
        parts.Add(tokenPanel);

        if (options != null)
        {
            var budgetPanel = BuildBudgetPanel(trace, options);
            if (budgetPanel != null) parts.Add(budgetPanel);
        }

        if (trace.ToolCalls.Count > 0)
        {
            var toolTable = new Table().Border(TableBorder.Rounded).Expand();
            toolTable.AddColumn("[cyan]Tool[/]");
            toolTable.AddColumn(new TableColumn("[yellow]Duration (ms)[/]").RightAligned());
            toolTable.AddColumn("[green]Status[/]");
            foreach (var tc in trace.ToolCalls)
            {
                var statusColor = tc.Status == "success" ? "green" : "red";
                toolTable.AddRow(
                    new Markup(Markup.Escape(tc.ToolName)),
                    new Markup($"{tc.DurationMs:F0}"),
                    new Markup($"[{statusColor}]{tc.Status}[/]"));
            }
            parts.Add(new Panel(toolTable).RoundedBorder().Header("Tool Calls").BorderColor(Color.Yellow).Padding(1, 1));
        }

        if (trace.Response != null)
        {
            var resp = trace.Response.Length > 300 ? trace.Response[..300] + "..." : trace.Response;
            parts.Add(new Panel(new Markup(Markup.Escape(resp)))
                .RoundedBorder().Header("Response Preview").BorderColor(Color.Grey).Padding(1, 1));
        }

        if (_traces.Count > 1)
        {
            var histTable = new Table().Border(TableBorder.Rounded).Expand();
            histTable.AddColumn("[grey]#[/]");
            histTable.AddColumn("[grey]Query[/]");
            histTable.AddColumn(new TableColumn("[grey]Time[/]").RightAligned());
            histTable.AddColumn(new TableColumn("[grey]Tokens[/]").RightAligned());

            for (int i = 0; i < _traces.Count; i++)
            {
                var t = _traces[i];
                var tMs = t.EndTime.HasValue ? (t.EndTime.Value - t.StartTime).TotalMilliseconds : 0;
                var isLast = i == _traces.Count - 1;
                var prefix = isLast ? "[bold]*[/]" : $" {i + 1}";
                histTable.AddRow(
                    new Markup(prefix),
                    new Markup(Markup.Escape(t.Query[..Math.Min(t.Query.Length, 40)])),
                    new Markup($"{tMs:F0}ms"),
                    new Markup($"{t.InputTokens + t.OutputTokens}"));
            }

            parts.Add(new Text(""));
            parts.Add(new Panel(histTable).RoundedBorder().Header("History").BorderColor(Color.Grey).Padding(1, 1));
        }

        var mainPanel = new Panel(new Rows(parts));
        mainPanel.Header = new PanelHeader("[cyan]Request Funnel[/]");
        mainPanel.Border = BoxBorder.Rounded;
        return mainPanel;
    }

    private static IRenderable? BuildBudgetPanel(RequestTrace trace, LTAIOptions options)
    {
        var pricing = options.ModelPricing;
        var modelName = trace.ModelName ?? "default";

        double inputPrice = pricing.InputPer1M.GetValueOrDefault(modelName, pricing.InputPer1M.GetValueOrDefault("default", 0.50));
        double outputPrice = pricing.OutputPer1M.GetValueOrDefault(modelName, pricing.OutputPer1M.GetValueOrDefault("default", 2.00));

        var cost = (trace.InputTokens / 1_000_000.0 * inputPrice) + (trace.OutputTokens / 1_000_000.0 * outputPrice);
        var dailyBudget = (double)options.AI.DailyBudgetUsd;

        return new Panel($$"""
            [cyan]Model:[/] {{Markup.Escape(modelName)}}
            [cyan]In Price:[/] ${{inputPrice:F4}}/1M tok
            [cyan]Out Price:[/] ${{outputPrice:F4}}/1M tok
            [cyan]Est. Cost:[/] ${{cost:F6}}
            [cyan]Daily Budget:[/] ${{dailyBudget:F2}}
            """)
            .RoundedBorder().Header("Budget Estimate").BorderColor(Color.Green).Padding(1, 1);
    }
}

internal sealed class RequestTrace
{
    public string Query { get; set; } = "";
    public string? Response { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public DateTime? LastStageTime { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public string? ModelLayer { get; set; }
    public string? ModelName { get; set; }
    public string? RouteConfidence { get; set; }
    public List<StageEntry> Stages { get; set; } = new();
    public List<ToolCallEntry> ToolCalls { get; set; } = new();
}

internal sealed record StageEntry(string Stage, string? Detail, double Ms);
internal sealed record ToolCallEntry(string ToolName, double DurationMs, string Status);
