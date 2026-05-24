using LTAI.AI.Governors;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LTAI.TUI;

public sealed class PipelineDashboard
{
    private readonly LivingTreeSystem _lts;

    private readonly Table _layersTable;
    private readonly Table _routingTable;
    private readonly Table _bavtTable;
    private readonly Table _erlTable;
    private readonly Table _elasticTable;
    private readonly Table _evolutionTable;
    private readonly Table _verifiableTable;

    private static readonly Style GreenStyle = new(Color.Green);
    private static readonly Style YellowStyle = new(Color.Yellow);
    private static readonly Style RedStyle = new(Color.Red);
    private static readonly Style CyanStyle = new(Color.Cyan1);
    private static readonly Style DimStyle = new(Color.Grey);

    public PipelineDashboard(LivingTreeSystem lts)
    {
        _lts = lts;

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

        return new Panel(new Rows(
            new Columns(topRow),
            new Columns(midRow),
            new Columns(bottomRow)))
            .Header(new PanelHeader("[bold cyan]Pipeline Dashboard[/]"))
            .BorderColor(Color.Cyan1)
            .Padding(1, 1);
    }
}
