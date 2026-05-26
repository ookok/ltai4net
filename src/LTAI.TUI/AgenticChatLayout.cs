using System.Linq;
using LTAI.Agent.MAF;
using LTAI.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LTAI.TUI;

public sealed class AgenticChatLayout
{
    private readonly AgenticLoop _loop;
    private readonly FunnelView? _funnelView;
    private readonly List<RenderablePart> _parts = new();
    private readonly Queue<string> _textBuffer = new();
    private string _reasoningText = "";
    private readonly Dictionary<string, ToolRender> _tools = new();
    private string _sessionId = "";
    private readonly Dictionary<string, DateTime> _toolStartTimes = new();

    private sealed class ToolRender
    {
        public string ToolName = "";
        public ToolState State = ToolState.Pending;
        public string? Output;
    }

    private sealed record RenderablePart(string Id, string Type, string Content);

    public AgenticChatLayout(AgenticLoop loop, FunnelView? funnelView = null)
    {
        _loop = loop;
        _funnelView = funnelView;
    }

    public async Task<string> ChatAsync(string task, CancellationToken ct = default)
    {
        _parts.Clear();
        _textBuffer.Clear();
        _reasoningText = "";
        _tools.Clear();
        _sessionId = _loop.SessionId;

        var assembler = _loop.PartAssembler;
        assembler.OnPartAppended += OnPartAppended;
        assembler.OnPartUpdated += OnPartUpdated;

        try
        {
            _funnelView?.RecordStage("ReAct Loop", "starting");

            await AnsiConsole.Live(new Panel("")).StartAsync(async ctx =>
            {
                var loopTask = _loop.RunAsync(task, ct);
                while (!loopTask.IsCompleted && !ct.IsCancellationRequested)
                {
                    ctx.UpdateTarget(BuildLayout(task));
                    await Task.Delay(100, ct);
                }
                ctx.UpdateTarget(BuildLayout(task));
                await loopTask;
            });

            var completedTools = _tools.Values.Count(t => t.State == ToolState.Completed);
            _funnelView?.SetTokenUsage(task.Length / 4, string.Join("\n", _textBuffer).Length / 4);

            return string.Join("\n", _textBuffer);
        }
        finally
        {
            assembler.OnPartAppended -= OnPartAppended;
            assembler.OnPartUpdated -= OnPartUpdated;
        }
    }

    private void OnPartAppended(Part part)
    {
        switch (part)
        {
            case TextPart text:
                _textBuffer.Enqueue(text.Text ?? "");
                while (_textBuffer.Count > 50) _textBuffer.TryDequeue(out _);
                break;
            case ReasoningPart reasoning:
                _reasoningText = reasoning.Text ?? "";
                break;
            case ToolInvocationPart tool:
                _tools[tool.Id] = new ToolRender { ToolName = tool.ToolName, State = tool.State };
                _toolStartTimes[tool.Id] = DateTime.UtcNow;
                break;
            case FilePart file:
                var change = file.ChangeType ?? "modified";
                var diagInfo = file.Diagnostics is { Length: > 0 }
                    ? $" ({file.Diagnostics.Length} diagnostics)"
                    : "";
                _textBuffer.Enqueue($"[File] {change}: {file.Path}{diagInfo}");
                break;
            case AgentPart agent:
                _textBuffer.Enqueue($"[SubAgent] {agent.AgentName}: {agent.Summary ?? "delegated"}");
                break;
        }
    }

    private void OnPartUpdated(Part part)
    {
        if (part is ToolInvocationPart tool && _tools.TryGetValue(tool.Id, out var tr))
        {
            var prevState = tr.State;
            tr.State = tool.State;
            tr.Output = tool.Output?.ToString();

            if (prevState != ToolState.Completed && tool.State == ToolState.Completed
                && _toolStartTimes.TryGetValue(tool.Id, out var start))
            {
                var duration = (DateTime.UtcNow - start).TotalMilliseconds;
                _funnelView?.RecordToolCall(tool.ToolName, duration, "success");
            }
            else if (prevState != ToolState.Error && tool.State == ToolState.Error
                && _toolStartTimes.TryGetValue(tool.Id, out var errStart))
            {
                var duration = (DateTime.UtcNow - errStart).TotalMilliseconds;
                _funnelView?.RecordToolCall(tool.ToolName, duration, "error");
            }
        }
    }

    private IRenderable BuildLayout(string task)
    {
        var rows = new List<IRenderable>();

        var headerLine = $"Step {_loop.IterationCount} — {DescribePhase()}";
        rows.Add(new Panel(new Markup($"[bold blue]LTAI AgenticLoop[/] — {Markup.Escape(task.Length > 80 ? task[..80] + "..." : task)}"))
            .Border(BoxBorder.Rounded).BorderColor(Color.Blue));

        rows.Add(new Markup($"[grey]{headerLine} | Session: {_sessionId}[/]"));

        if (_tools.Count > 0)
        {
            var completedTools = _tools.Values.Count(t => t.State == ToolState.Completed);
            var stepsLine = "";
            if (completedTools > 0)
                stepsLine += $"[blue]Progress:[/] {completedTools}/{_tools.Count} tools complete  ";
            stepsLine += GetAnimatedIndicator();
            rows.Add(new Markup(stepsLine));
        }

        if (!string.IsNullOrEmpty(_reasoningText))
        {
            var reasoningPanel = new Panel(
                new Markup($"[grey]{Markup.Escape(_reasoningText.Length > 500 ? _reasoningText[..500] + "..." : _reasoningText)}[/]"))
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Grey)
                .Header("Reasoning");
            rows.Add(reasoningPanel);
        }

        if (_tools.Count > 0)
        {
            var toolsTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Yellow);
            toolsTable.AddColumn("Tool");
            toolsTable.AddColumn("State");
            toolsTable.AddColumn("Output");
            foreach (var (_, tr) in _tools)
            {
                var stateIcon = tr.State switch
                {
                    ToolState.Completed => "✓",
                    ToolState.Error => "✗",
                    ToolState.Executing => "⏳",
                    ToolState.Pending => "○",
                    _ => "?"
                };
                var stateColor = tr.State switch
                {
                    ToolState.Completed => $"[green]{stateIcon} Completed[/]",
                    ToolState.Error => $"[red]{stateIcon} Error[/]",
                    ToolState.Executing => $"[yellow]{stateIcon} Executing[/]",
                    _ => $"[grey]{stateIcon} Pending[/]"
                };
                var output = tr.Output != null && tr.Output.Length > 60
                    ? Markup.Escape(tr.Output[..60] + "...")
                    : Markup.Escape(tr.Output ?? "");
                toolsTable.AddRow(Markup.Escape(tr.ToolName), stateColor, output);
            }
            var toolsPanel = new Panel(toolsTable).Header("Tool Invocations");
            rows.Add(toolsPanel);
        }

        var textContent = string.Join("\n", _textBuffer.TakeLast(30));
        if (!string.IsNullOrEmpty(textContent))
        {
            var responsePanel = new Panel(
                new Markup(Markup.Escape(textContent.Length > 2000 ? textContent[..2000] + "\n[dim]...[/]" : textContent)))
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Green)
                .Header("Response");
            rows.Add(responsePanel);
        }

        return new Rows(rows);
    }

    private string DescribePhase()
    {
        if (_tools.Values.Any(t => t.State == ToolState.Executing))
            return "Executing tools";
        if (_tools.Values.Any(t => t.State == ToolState.Completed))
            return "Processing results";
        if (!string.IsNullOrEmpty(_reasoningText))
            return "Reasoning";
        if (_textBuffer.Count > 0)
            return "Generating response";
        return "Analyzing task";
    }

    private static string GetAnimatedIndicator()
    {
        var frame = Environment.TickCount / 300 % 4;
        return frame switch
        {
            0 => "[cyan]◐[/] Processing",
            1 => "[cyan]◓[/] Processing",
            2 => "[cyan]◑[/] Processing",
            3 => "[cyan]◒[/] Processing",
            _ => ""
        };
    }
}
