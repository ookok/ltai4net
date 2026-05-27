using LTAI.AI.Governors;
using LTAI.AI.Interfaces;
using LTAI.Core.Governors;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LTAI.TUI;

public sealed class PipelineDashboard
{
    private readonly ILivingTreeSystem _lts;
    private readonly CPSProcessingService? _cps;
    private readonly CoordinationScheduler? _scheduler;
    private readonly ParetoRouter? _router;
    private readonly IMicroKernel? _kernel;

    private readonly Table _layersTable;
    private readonly Table _routingTable;
    private readonly Table _bavtTable;
    private readonly Table _erlTable;
    private readonly Table _elasticTable;
    private readonly Table _evolutionTable;
    private readonly Table _verifiableTable;
    private readonly Table _cpsTable;
    private readonly Table _healthTable;
    private readonly Table _paretoTable;

    private static readonly Style GreenStyle = new(Color.Green);
    private static readonly Style YellowStyle = new(Color.Yellow);
    private static readonly Style RedStyle = new(Color.Red);
    private static readonly Style CyanStyle = new(Color.Cyan1);
    private static readonly Style DimStyle = new(Color.Grey);

    public PipelineDashboard(ILivingTreeSystem lts,
        CPSProcessingService? cps = null,
        CoordinationScheduler? scheduler = null,
        ParetoRouter? router = null,
        IMicroKernel? kernel = null)
    {
        _lts = lts;
        _cps = cps;
        _scheduler = scheduler;
        _router = router;
        _kernel = kernel;

        _layersTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Cyan1)
            .AddColumn("Layer").AddColumn("Model").AddColumn("Status");
        _routingTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Yellow)
            .AddColumn("Check").AddColumn("Result");
        _bavtTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Green)
            .AddColumn("Metric").AddColumn("Value");
        _erlTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Blue)
            .AddColumn("Metric").AddColumn("Value");
        _elasticTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Magenta1)
            .AddColumn("Metric").AddColumn("Value");
        _evolutionTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Yellow)
            .AddColumn("Metric").AddColumn("Value");
        _verifiableTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Green)
            .AddColumn("Metric").AddColumn("Value");

        _cpsTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Aqua)
            .AddColumn("Metric").AddColumn("Value");

        _healthTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Orange1)
            .AddColumn("Metric").AddColumn("Value");

        _paretoTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Purple)
            .AddColumn("Metric").AddColumn("Value");
    }

    public async Task ShowAsync(CancellationToken ct = default)
    {
        var layout = BuildLayout();

        await AnsiConsole.Live(layout)
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .StartAsync(async ctx =>
            {
                while (!ct.IsCancellationRequested)
                {
                    RefreshSnapshot();
                    ctx.UpdateTarget(BuildLayout());
                    await Task.Delay(2000, ct);
                }
            });
    }

    private void RefreshSnapshot()
    {
        var snap = new Dictionary<string, object>();
        Snapshot(snap);
        UpdateTables(snap);
    }

    private void Snapshot(Dictionary<string, object> snap)
    {
#pragma warning disable CS8601
        try
        {
            snap["system.mode"] = _lts.Mode.ToString();
            snap["system.dna"] = _lts.DNAEnabled;
            var flashModel = _lts.GetType().GetProperty("FlashModel",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(_lts)?.ToString() ?? "?";
            var deepModel = _lts.GetType().GetProperty("DefaultModel",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(_lts)?.ToString() ?? "?";
            snap["system.l1"] = flashModel;
            snap["system.l2"] = deepModel;
        }
        catch { }

        try
        {
            var bavt = _lts.GetType().GetField("_bavtRouter",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(_lts);
            if (bavt != null)
            {
                snap["bavt.ratio"] = bavt.GetType().GetProperty("BudgetRatio")?.GetValue(bavt);
                snap["bavt.remaining"] = bavt.GetType().GetProperty("RemainingBudget")?.GetValue(bavt);
                snap["bavt.spent"] = bavt.GetType().GetProperty("TotalSpent")?.GetValue(bavt);
            }
        }
        catch { }

        try
        {
            var erl = _lts.GetType().GetField("_erlLoop",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(_lts);
            if (erl != null)
            {
                snap["erl.trials"] = erl.GetType().GetProperty("TotalTrials")?.GetValue(erl);
                snap["erl.rate"] = erl.GetType().GetProperty("SuccessRate")?.GetValue(erl);
            }
        }
        catch { }

        try
        {
            var elastic = _lts.GetType().GetField("_elasticMemory",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(_lts);
            if (elastic != null)
            {
                var statsObj = elastic.GetType().GetProperty("Stats")?.GetValue(elastic);
                if (statsObj != null)
                {
                    var t = statsObj.GetType();
                    snap["elastic.raw"] = t.GetField("Item1")?.GetValue(statsObj);
                    snap["elastic.compressed"] = t.GetField("Item2")?.GetValue(statsObj);
                    snap["elastic.episodic"] = t.GetField("Item3")?.GetValue(statsObj);
                }
            }
        }
        catch { }

        try
        {
            var evolution = _lts.GetType().GetField("_evolutionStore",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(_lts) as ICrossRunEvolutionStore;
            if (evolution != null)
            {
                snap["evolution.total"] = evolution.LessonCount;
                snap["evolution.active"] = evolution.ActiveLessonCount;
            }
        }
        catch { }

        try
        {
            var verifiable = _lts.GetType().GetField("_verifiableRegistry",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(_lts) as IVerifiableRegistry;
            if (verifiable != null)
            {
                snap["verifiable.measurements"] = verifiable.MeasurementCount;
                snap["verifiable.citations"] = verifiable.VerifiedCitationCount;
            }
        }
        catch { }

        if (_cps != null)
        {
            try
            {
                var stats = _cps.GetPerformanceStats();
                snap["cps.total"] = stats.TotalProcessed;
                snap["cps.latency"] = $"{stats.AvgLatencyMs}ms";
                snap["cps.tokens"] = stats.EstimatedTotalTokens;
                snap["cps.routes"] = string.Join(",", stats.RouteDistribution.Select(kv => $"{kv.Key}:{kv.Value}"));
            }
            catch { }
        }

        if (_scheduler != null)
        {
            try
            {
                snap["scheduler.running"] = _scheduler.IsRunning;
                snap["scheduler.queue"] = _scheduler.QueueDepth;
                snap["scheduler.events"] = _scheduler.EventsProcessed;
                snap["scheduler.rules"] = _scheduler.RulesTriggered;
            }
            catch { }
        }

        if (_router != null)
        {
            try
            {
                snap["pareto.size"] = _router.FrontierSize;
                snap["pareto.decisions"] = _router.TotalDecisions;
                snap["pareto.shadow"] = $"{_router.ShadowRate:P0}";
            }
            catch { }
        }

        if (_kernel != null)
        {
            try
            {
                snap["kernel.healthy"] = _kernel.IsHealthy;
                var vitals = _kernel.GetAggregatedVitals();
                snap["kernel.p50"] = $"{vitals.P50LatencyMs}ms";
                snap["kernel.p99"] = $"{vitals.P99LatencyMs}ms";
            }
            catch { }
        }
#pragma warning restore CS8601
    }

    private void UpdateTables(Dictionary<string, object> snap)
    {
        _layersTable.Rows.Clear();
        _layersTable.AddRow("L0", Markup.Escape(snap.GetValueOrDefault("system.l1")?.ToString() ?? "?"), "[dim]embedding[/]");
        _layersTable.AddRow("L1", Markup.Escape(snap.GetValueOrDefault("system.l1")?.ToString() ?? "?"), "[yellow]fast[/]");
        _layersTable.AddRow("L2", Markup.Escape(snap.GetValueOrDefault("system.l2")?.ToString() ?? "?"), "[cyan]deep[/]");
        _layersTable.AddRow("ONNX", "", snap.GetValueOrDefault("system.onnx")?.ToString() == "True" ? "[green]enabled[/]" : "[dim]disabled[/]");
        _layersTable.AddRow("DNA", "", snap.GetValueOrDefault("system.dna")?.ToString() == "True" ? "[green]active[/]" : "[dim]inactive[/]");

        _routingTable.Rows.Clear();
        _routingTable.AddRow("[dim]Router[/]", $"[dim]{snap.GetValueOrDefault("system.mode")?.ToString() ?? "?"}[/]");

        _bavtTable.Rows.Clear();
        var ratio = snap.GetValueOrDefault("bavt.ratio");
        var ratioStr = ratio is double d ? $"{d:P1}" : ratio?.ToString() ?? "?";
        _bavtTable.AddRow("Budget Ratio", ratioStr);
        _bavtTable.AddRow("Remaining", snap.GetValueOrDefault("bavt.remaining")?.ToString() ?? "?");
        _bavtTable.AddRow("Spent", snap.GetValueOrDefault("bavt.spent")?.ToString() ?? "?");

        _erlTable.Rows.Clear();
        _erlTable.AddRow("Trials", snap.GetValueOrDefault("erl.trials")?.ToString() ?? "0");
        var rate = snap.GetValueOrDefault("erl.rate");
        var rateStr = rate is double rd ? $"{rd:P1}" : rate?.ToString() ?? "0";
        _erlTable.AddRow("Success Rate", rateStr);

        _elasticTable.Rows.Clear();
        _elasticTable.AddRow("Raw", snap.GetValueOrDefault("elastic.raw")?.ToString() ?? "0");
        _elasticTable.AddRow("Compressed", snap.GetValueOrDefault("elastic.compressed")?.ToString() ?? "0");
        _elasticTable.AddRow("Episodic", snap.GetValueOrDefault("elastic.episodic")?.ToString() ?? "0");

        _evolutionTable.Rows.Clear();
        _evolutionTable.AddRow("Total Lessons", snap.GetValueOrDefault("evolution.total")?.ToString() ?? "0");
        _evolutionTable.AddRow("Active", snap.GetValueOrDefault("evolution.active")?.ToString() ?? "0");

        _verifiableTable.Rows.Clear();
        _verifiableTable.AddRow("Measurements", snap.GetValueOrDefault("verifiable.measurements")?.ToString() ?? "0");
        _verifiableTable.AddRow("Citations", snap.GetValueOrDefault("verifiable.citations")?.ToString() ?? "0");

        _cpsTable.Rows.Clear();
        _cpsTable.AddRow("Processed", snap.GetValueOrDefault("cps.total")?.ToString() ?? "0");
        _cpsTable.AddRow("Avg Latency", snap.GetValueOrDefault("cps.latency")?.ToString() ?? "?");
        _cpsTable.AddRow("Est. Tokens", snap.GetValueOrDefault("cps.tokens")?.ToString() ?? "0");
        _cpsTable.AddRow("Routes", snap.GetValueOrDefault("cps.routes")?.ToString() ?? "?");

        _healthTable.Rows.Clear();
        var running = snap.GetValueOrDefault("scheduler.running") is true;
        _healthTable.AddRow("Scheduler", running ? "[green]running[/]" : "[red]stopped[/]");
        _healthTable.AddRow("Queue Depth", snap.GetValueOrDefault("scheduler.queue")?.ToString() ?? "?");
        _healthTable.AddRow("Events", snap.GetValueOrDefault("scheduler.events")?.ToString() ?? "0");
        _healthTable.AddRow("Kernel", snap.GetValueOrDefault("kernel.healthy") is true ? "[green]healthy[/]" : "[red]degraded[/]");
        _healthTable.AddRow("P50", snap.GetValueOrDefault("kernel.p50")?.ToString() ?? "?");
        _healthTable.AddRow("P99", snap.GetValueOrDefault("kernel.p99")?.ToString() ?? "?");

        _paretoTable.Rows.Clear();
        _paretoTable.AddRow("Frontier Size", snap.GetValueOrDefault("pareto.size")?.ToString() ?? "0");
        _paretoTable.AddRow("Decisions", snap.GetValueOrDefault("pareto.decisions")?.ToString() ?? "0");
        _paretoTable.AddRow("Shadow Rate", snap.GetValueOrDefault("pareto.shadow")?.ToString() ?? "?");
    }

    private IRenderable BuildLayout()
    {
        RefreshSnapshot();

        var topRow = new List<IRenderable>();
        topRow.Add(new Panel(_layersTable).Header("Layers").BorderColor(Color.Cyan1).Padding(1, 1));
        topRow.Add(new Panel(_routingTable).Header("Routing").BorderColor(Color.Yellow).Padding(1, 1));
        topRow.Add(new Panel(_bavtTable).Header("BAVT Budget").BorderColor(Color.Green).Padding(1, 1));

        var midRow = new List<IRenderable>();
        midRow.Add(new Panel(_erlTable).Header("ERL Trials").BorderColor(Color.Blue).Padding(1, 1));
        midRow.Add(new Panel(_elasticTable).Header("Elastic Memory").BorderColor(Color.Magenta1).Padding(1, 1));

        var bottomRow = new List<IRenderable>();
        bottomRow.Add(new Panel(_evolutionTable).Header("Cross-Run Evolution").BorderColor(Color.Yellow).Padding(1, 1));
        bottomRow.Add(new Panel(_verifiableTable).Header("Verifiable Registry").BorderColor(Color.Green).Padding(1, 1));

        var cpsRow = new List<IRenderable>();
        cpsRow.Add(new Panel(_cpsTable).Header("CPS Performance").BorderColor(Color.Aqua).Padding(1, 1));
        cpsRow.Add(new Panel(_healthTable).Header("System Health").BorderColor(Color.Orange1).Padding(1, 1));
        cpsRow.Add(new Panel(_paretoTable).Header("Pareto Router").BorderColor(Color.Purple).Padding(1, 1));

        return new Panel(new Rows(
            new Columns(topRow),
            new Columns(midRow),
            new Columns(bottomRow),
            new Columns(cpsRow)))
            .Header(new PanelHeader("[bold cyan]Pipeline Dashboard[/]"))
            .BorderColor(Color.Cyan1)
            .Padding(1, 1);
    }
}
