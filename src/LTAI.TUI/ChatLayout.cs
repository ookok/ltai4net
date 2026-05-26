using System.Text;
using LTAI.AI.Interfaces;
using LTAI.AI.Governors;
using LTAI.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LTAI.TUI;

public sealed class ChatLayout
{
    private readonly ILivingTreeSystem _lts;
    private readonly LTAIOptions? _options;
    private readonly List<(string role, string content)> _history = new();
    private readonly string? _loadedFileContent;

    private readonly Table _routingTable;
    private readonly Table _toolsTable;
    private readonly StringBuilder _responseBuffer = new();
    private readonly StringBuilder _thinkingBuffer = new();
    private string _routeLabel = "routing...";
    private string _routeConfidence = "";
    private bool _hasResponse;
    private bool _hasThinking;

    public ChatLayout(ILivingTreeSystem lts, LTAIOptions? options, string? loadedFileContent = null)
    {
        _lts = lts;
        _options = options;
        _loadedFileContent = loadedFileContent;

        _routingTable = new Table().Border(TableBorder.None).HideHeaders().Expand()
            .AddColumn("").AddColumn("");
        _toolsTable = new Table().Border(TableBorder.None).HideHeaders().Expand()
            .AddColumn("").AddColumn("").AddColumn("");
    }

    public async Task<string> ChatAsync(string input, string? modelOverride = null)
    {
        _responseBuffer.Clear();
        _thinkingBuffer.Clear();
        _hasResponse = false;
        _hasThinking = false;
        _routeLabel = "routing...";
        _routeConfidence = "";

        _routingTable.Rows.Clear();
        _routingTable.AddRow(
            new Markup($"[dim]Router[/] → [yellow]{_routeLabel}[/] {_routeConfidence}"),
            new Markup("[dim]KG[/]: [grey]querying...[/]"));
        _routingTable.AddRow(
            new Markup("[dim]Binary[/]: [grey]searching...[/]"),
            new Markup("[dim]PACE[/]: [grey]evaluating...[/]"));

        _toolsTable.Rows.Clear();

        var fullResponse = "";
        var layout = BuildLayout();

        await AnsiConsole.Live(layout).AutoClear(false).StartAsync(async ctx =>
        {
            await foreach (var chunk in _lts.StreamChatAsync(input, modelOverride))
            {
                if (chunk.StartsWith("<thinking>") && chunk.EndsWith("</thinking>"))
                {
                    _thinkingBuffer.Append(chunk.AsSpan(10, chunk.Length - 21));
                    _hasThinking = true;
                }
                else
                {
                    _responseBuffer.Append(chunk);
                    _hasResponse = true;
                }
                layout = BuildLayout();
                ctx.UpdateTarget(layout);
            }

            fullResponse = _responseBuffer.ToString();
            _history.Add(("You", input));
            _history.Add(("LTAI", fullResponse));
        });

        return fullResponse;
    }

    private IRenderable BuildLayout()
    {
        var rows = new List<IRenderable>();

        var l1 = _options?.AI.L1.Model ?? "?";
        var l2 = _options?.AI.L2.Model ?? "?";
        rows.Add(new Panel(new Markup($"[bold cyan]LTAI Chat[/]  [dim]L1:{Markup.Escape(l1)} L2:{Markup.Escape(l2)}[/]"))
            .RoundedBorder().BorderColor(Color.Cyan1).Padding(1, 0));

        if (_history.Count > 0)
        {
            var histItems = new List<IRenderable>();
            var start = Math.Max(0, _history.Count - 4);
            for (int i = start; i < _history.Count; i++)
            {
                var (role, content) = _history[i];
                var color = role == "You" ? "green" : "white";
                histItems.Add(new Markup($"[bold {color}]{role}:[/] {Markup.Escape(content.Length > 200 ? content[..200] + "..." : content)}"));
                if (i < _history.Count - 1) histItems.Add(new Text(""));
            }
            rows.Add(new Panel(new Rows(histItems)).RoundedBorder().Header("History").BorderColor(Color.Grey).Padding(1, 1));
        }

        if (_hasThinking)
        {
            var t = _thinkingBuffer.ToString();
            rows.Add(new Panel(new Markup($"[dim]{Markup.Escape(t.Length > 400 ? t[..400] + "..." : t)}[/]"))
                .RoundedBorder().Header("LLM Reasoning").BorderColor(Color.Grey).Padding(1, 1));
        }

        rows.Add(new Panel(_routingTable).RoundedBorder().Header("Routing").BorderColor(Color.Yellow).Padding(1, 1));

        if (_toolsTable.Rows.Count > 0)
            rows.Add(new Panel(_toolsTable).RoundedBorder().Header("Tools").BorderColor(Color.Blue).Padding(1, 1));

        if (_hasResponse)
        {
            var t = _responseBuffer.ToString();
            rows.Add(new Panel(new Markup(Markup.Escape(t.Length > 2000 ? t[..2000] + "\n[dim]...[/]" : t)))
                .RoundedBorder().Header("Response").BorderColor(Color.Green).Padding(1, 1));
        }

        rows.Add(new Panel(new Markup($"[dim]{(l1)} / {(l2)}[/]"))
            .RoundedBorder().BorderColor(Color.Grey).Padding(1, 0));

        return new Rows(rows);
    }

    public void UpdateRouteInfo(string route, string confidence)
    {
        _routeLabel = route;
        _routeConfidence = confidence;
    }

    public void AddToolCall(string toolName, string status)
    {
        var color = status switch { "success" => "green", "error" => "red", _ => "yellow" };
        _toolsTable.AddRow(new Markup($"[{color}]{status}[/]"), new Markup($"[dim]{Markup.Escape(toolName)}[/]"), new Markup(""));
    }
}
