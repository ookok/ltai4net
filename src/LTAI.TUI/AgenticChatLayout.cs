using LTAI.Agent.MAF;
using LTAI.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LTAI.TUI;

public sealed class AgenticChatLayout
{
    private readonly AgenticLoop _loop;
    private readonly List<RenderablePart> _parts = new();
    private readonly Queue<string> _textBuffer = new();
    private string _reasoningText = "";
    private readonly Dictionary<string, ToolRender> _tools = new();
    private string _sessionId = "";

    private sealed class ToolRender
    {
        public string ToolName = "";
        public ToolState State = ToolState.Pending;
        public string? Output;
    }

    private sealed record RenderablePart(string Id, string Type, string Content);

    public AgenticChatLayout(AgenticLoop loop)
    {
        _loop = loop;
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
            tr.State = tool.State;
            tr.Output = tool.Output?.ToString();
        }
    }

    private IRenderable BuildLayout(string task)
    {
        var rows = new List<IRenderable>();

        rows.Add(new Panel(new Markup($"[bold blue]LTAI AgenticLoop[/] — {Markup.Escape(task.Length > 80 ? task[..80] + "..." : task)}"))
            .Border(BoxBorder.Rounded).BorderColor(Color.Blue));

        rows.Add(new Markup($"[grey]Iteration: {_loop.IterationCount} | Session: {_sessionId}[/]"));

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
                var stateColor = tr.State switch
                {
                    ToolState.Completed => "[green]Completed[/]",
                    ToolState.Error => "[red]Error[/]",
                    ToolState.Executing => "[yellow]Executing[/]",
                    _ => "[grey]Pending[/]"
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
}
