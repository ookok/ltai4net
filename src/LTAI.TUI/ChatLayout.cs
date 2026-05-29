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
    // FunnelView removed — TUI simplified

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
        // _funnelView removed

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
        var modelName = modelOverride ?? _options?.AI.GetLayerConfig("deep").Model ?? "?";

        await AnsiConsole.Live(layout).AutoClear(false).StartAsync(async ctx =>
        {
            // FunnelView removed

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

    public void Render()
    {
        AnsiConsole.Write(BuildLayout());
    }

    private IRenderable BuildLayout()
    {
        var rows = new List<IRenderable>();

        var l1 = _options?.AI.GetLayerConfig("fast").Model ?? "?";
        var l2 = _options?.AI.GetLayerConfig("deep").Model ?? "?";
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
            rows.Add(new Panel(new Rows(RenderMarkdown(t)))
                .RoundedBorder().Header("Response").BorderColor(Color.Green).Padding(1, 1));
        }

        rows.Add(new Panel(new Markup($"[dim]{Markup.Escape(l1)} / {Markup.Escape(l2)}[/]"))
            .RoundedBorder().BorderColor(Color.Grey).Padding(1, 0));

        return new Rows(rows);
    }

    /// <summary>
    /// Convert markdown text to Spectre.Console renderables.
    /// Supports: code fences, **bold**, *italic*, inline `code`, ### headings.
    /// </summary>
    private static List<IRenderable> RenderMarkdown(string text, int maxLength = 8000)
    {
        if (string.IsNullOrEmpty(text))
            return new List<IRenderable> { new Markup("") };

        if (text.Length > maxLength)
            text = text[..maxLength] + "\n[dim]... (truncated)[/]";

        var result = new List<IRenderable>();
        var lines = text.Split('\n');
        var inCodeBlock = false;
        var codeLines = new List<string>();
        var codeLanguage = "";

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```"))
            {
                if (inCodeBlock)
                {
                    var codeText = string.Join("\n", codeLines);
                    var panel = new Panel(new Text(codeText, new Style(foreground: Color.Silver)))
                        .RoundedBorder().BorderColor(Color.Blue).Padding(1, 0);
                    if (codeLanguage.Length > 0)
                        panel = panel.Header($"[dim]{Markup.Escape(codeLanguage)}[/]", Justify.Left);
                    result.Add(panel);
                    codeLines.Clear();
                    codeLanguage = "";
                    inCodeBlock = false;
                }
                else
                {
                    inCodeBlock = true;
                    codeLanguage = line.TrimStart()[3..].Trim();
                }
                continue;
            }

            if (inCodeBlock)
            {
                codeLines.Add(line);
                continue;
            }

            result.Add(new Markup(ProcessInlineMarkdown(line)));
        }

        if (inCodeBlock && codeLines.Count > 0)
        {
            var panel = new Panel(new Text(string.Join("\n", codeLines), new Style(foreground: Color.Silver)))
                .RoundedBorder().BorderColor(Color.Blue).Padding(1, 0);
            result.Add(panel);
        }

        return result;
    }

    /// <summary>Apply markdown inline formatting on an escaped line.</summary>
    private static string ProcessInlineMarkdown(string rawLine)
    {
        // Escape Spectre markup special chars first, then apply markdown patterns
        var s = rawLine;

        // Headings (# ## ###)
        var trimmed = s.TrimStart();
        if (trimmed.StartsWith("### ") || trimmed.StartsWith("## ") || trimmed.StartsWith("# "))
        {
            var level = trimmed.TakeWhile(c => c == '#').Count();
            var text = trimmed[(level + 1)..];
            s = s[..(s.Length - trimmed.Length)] + $"[bold underline]{Markup.Escape(text)}[/]";
            return s;
        }

        // Must escape before applying markdown patterns to avoid Spectre parsing issues
        s = Markup.Escape(s);

        // Bold: **text** → [bold]text[/]
        s = ReplaceMd(s, "**", "bold");

        // Italic: *text* → [italic]text[/]
        s = ReplaceMd(s, "*", "italic");

        // Inline code: `text` → [italic lime]text[/]
        s = ReplaceMd(s, "`", "italic lime");

        return s;
    }

    /// <summary>Replace markdown paired delimiters with Spectre markup tags.</summary>
    private static string ReplaceMd(string text, string marker, string spectreStyle)
    {
        int idx = 0;
        while (true)
        {
            var open = text.IndexOf(marker, idx, StringComparison.Ordinal);
            if (open < 0) break;

            var close = text.IndexOf(marker, open + marker.Length, StringComparison.Ordinal);
            if (close < 0) break;

            var inner = text[(open + marker.Length)..close];
            if (string.IsNullOrWhiteSpace(inner))
            {
                idx = close + marker.Length;
                continue;
            }

            var replacement = $"[{spectreStyle}]{inner}[/]";
            text = text[..open] + replacement + text[(close + marker.Length)..];
            idx = open + replacement.Length;
        }
        return text;
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
