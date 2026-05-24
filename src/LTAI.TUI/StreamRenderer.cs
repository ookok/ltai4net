using Spectre.Console.Rendering;
using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace LTAI.TUI;

public sealed class StreamRenderer
{
    private readonly StringBuilder _buffer = new();
    private readonly List<StreamBlock> _blocks = new();
    private int _consoleWidth = 80;
    private StreamBlock? _currentBlock;
    private readonly string? _originalContext;
    private DiffResult? _currentDiff;
    private bool _showDiff = true;
    private DiffMode _diffMode = DiffMode.Unified;

    private const int CodeBlockMinLines = 2;

    public StreamRenderer(string? originalContext = null)
    {
        _originalContext = originalContext;
    }

    public void ToggleDiffMode() => _showDiff = !_showDiff;
    public void CycleDiffMode() => _diffMode = _diffMode == DiffMode.Unified ? DiffMode.Split : DiffMode.Unified;

    public async Task RenderStreamAsync(
        IAsyncEnumerable<string> tokenStream,
        CancellationToken cancellationToken = default)
    {
        _buffer.Clear();
        _blocks.Clear();
        _currentBlock = null;
        _consoleWidth = Console.WindowWidth > 0 ? Console.WindowWidth : 80;

        _currentBlock = new StreamBlock(BlockType.Text, 0);

        await AnsiConsole.Live(new Text(""))
            .StartAsync(async ctx =>
            {
                var markdown = new MarkdownRenderer();

                await foreach (var token in tokenStream.WithCancellation(cancellationToken))
                {
                    _buffer.Append(token);
                    ProcessToken(token);

                    var rendered = RenderBlocks(markdown);
                    ctx.UpdateTarget(rendered);
                }

                FinalizeBlocks();
                ctx.UpdateTarget(RenderBlocks(markdown));
            });
    }

    private void ProcessToken(string token)
    {
        if (token.StartsWith("<thinking>") && token.EndsWith("</thinking>"))
        {
            HandleThinkingToken(token);
        }
        else if (token.Contains("```"))
        {
            HandleCodeBlockBoundary(token);
        }
        else if (token.StartsWith("[tool:") || token.Contains("[tool:"))
        {
            HandleToolCall(token);
        }
        else if (token.Contains(" response") && _currentBlock?.Type == BlockType.Tool)
        {
            HandleToolResult(token);
        }
        else
        {
            if (_currentBlock?.Type == BlockType.Thinking)
                CompleteCurrentBlock();

            _currentBlock ??= new StreamBlock(BlockType.Text, _blocks.Count);
            _currentBlock.Append(token);
            CheckMarkdownBoundaries();
        }
    }

    private void HandleThinkingToken(string token)
    {
        if (_currentBlock?.Type == BlockType.Text)
            CompleteCurrentBlock();

        var content = token.AsSpan(10, token.Length - 21).ToString();

        if (_currentBlock?.Type == BlockType.Thinking)
            _currentBlock.Append(content);
        else
        {
            CompleteCurrentBlock();
            _currentBlock = new StreamBlock(BlockType.Thinking, _blocks.Count);
            _currentBlock.Append(content);
        }
    }

    private void HandleCodeBlockBoundary(string token)
    {
        if (_currentBlock?.Type == BlockType.Code)
        {
            FinalizeCurrentBlock();
            _currentBlock = new StreamBlock(BlockType.Text, _blocks.Count);
        }
        else
        {
            FinalizeCurrentBlock();
            var lang = "";
            var langMatch = Regex.Match(token, @"```(\w+)");
            if (langMatch.Success) lang = langMatch.Groups[1].Value;
            _currentBlock = new StreamBlock(BlockType.Code, _blocks.Count) { Language = lang };
        }
    }

    private void HandleToolCall(string token)
    {
        FinalizeCurrentBlock();
        var name = Regex.Match(token, @"\[tool:(\w+)\]") is { Success: true } m ? m.Groups[1].Value : "unknown";
        _currentBlock = new StreamBlock(BlockType.Tool, _blocks.Count)
        {
            ToolName = name,
            ToolStatus = "running"
        };
    }

    private void HandleToolResult(string token)
    {
        if (_currentBlock?.Type != BlockType.Tool) return;

        var result = token;
        var match = Regex.Match(token, @"result[:\s]*(.*)");
        if (match.Success) result = match.Groups[1].Value;

        _currentBlock.ToolStatus = "done";
        _currentBlock.ToolResult = result[..Math.Min(result.Length, 200)];
        CompleteCurrentBlock();
    }

    private void CheckMarkdownBoundaries()
    {
        if (_currentBlock == null) return;
        var text = _currentBlock.Text;

        if (text.StartsWith("#") && text.Contains("\n"))
        {
            var lines = text.Split('\n');
            if (lines.Length >= 2 && !string.IsNullOrWhiteSpace(lines[1]))
            {
                FinalizeCurrentBlock();
                _currentBlock = new StreamBlock(BlockType.Text, _blocks.Count);
                _currentBlock.Append(text);
            }
        }
    }

    private IRenderable RenderBlocks(MarkdownRenderer markdown)
    {
        var layout = new List<IRenderable>();

        foreach (var block in _blocks)
        {
            layout.Add(RenderBlock(block, markdown));
        }

        if (_currentBlock != null)
        {
            layout.Add(RenderBlock(_currentBlock, markdown));
        }

        if (layout.Count == 0)
            return new Text("");

        var rows = new Rows(layout);
        return rows;
    }

    private IRenderable RenderBlock(StreamBlock block, MarkdownRenderer markdown)
    {
        return block.Type switch
        {
            BlockType.Text => RenderTextBlock(block, markdown),
            BlockType.Code => RenderCodeBlock(block),
            BlockType.Tool => RenderToolBlock(block),
            _ => new Text(block.Text)
        };
    }

    private IRenderable RenderTextBlock(StreamBlock block, MarkdownRenderer md)
    {
        var text = block.Text;
        if (string.IsNullOrEmpty(text)) return new Text("");

        text = md.Transform(text);

        if (!block.IsComplete)
        {
            text += (DateTime.Now.Millisecond % 1000 < 500) ? "[cyan]▌[/]" : " ";
        }

        return new Markup(text);
    }

    private IRenderable RenderCodeBlock(StreamBlock block)
    {
        var code = block.Text.Trim('\n', '\r');
        if (string.IsNullOrEmpty(code)) return new Text("");

        if (_showDiff && _originalContext != null && block.Type == BlockType.Code)
        {
            var diff = DiffEngine.Compute(_originalContext, code);
            if (diff.AddedCount + diff.RemovedCount + diff.ChangedCount > 0)
            {
                _currentDiff = diff;
                return RenderDiffBlock(diff, block.Language);
            }
        }

        var header = string.IsNullOrEmpty(block.Language) ? "[grey]Code[/]" : $"[grey]{Escape(block.Language)}[/]";
        var lines = code.Split('\n');
        var numberedCode = new StringBuilder();
        var maxLine = lines.Length;

        for (var i = 0; i < Math.Min(lines.Length, 30); i++)
        {
            var num = (i + 1).ToString().PadLeft(maxLine.ToString().Length);
            numberedCode.AppendLine($"[grey]{num}[/] [white]{Escape(lines[i])}[/]");
        }

        if (lines.Length > 30)
            numberedCode.AppendLine($"[grey]... {lines.Length - 30} more lines[/]");

        return new Panel(new Markup(numberedCode.ToString().TrimEnd()))
        {
            Border = BoxBorder.Rounded,
            Header = new PanelHeader(header)
        };
    }

    private IRenderable RenderDiffBlock(DiffResult diff, string language)
    {
        var header = string.IsNullOrEmpty(language) ? "Diff" : $"{language} diff";
        var stats = $"[red]-{diff.RemovedCount}[/] [green]+{diff.AddedCount}[/] [yellow]~{diff.ChangedCount}[/]";
        var diffText = DiffEngine.RenderUnifiedDiff(diff);

        var panel = new Panel(new Markup(diffText))
        {
            Border = BoxBorder.Rounded,
            Header = new PanelHeader($"{header}  {stats}")
        };
        return panel;
    }

    private IRenderable RenderToolBlock(StreamBlock block)
    {
        var status = block.ToolStatus switch
        {
            "running" => "[yellow]⏳[/]",
            "done" => "[green]✓[/]",
            "error" => "[red]✗[/]",
            _ => "[grey]?[/]"
        };

        var name = $"[cyan]{block.ToolName}[/]";

        if (block.IsComplete && !string.IsNullOrEmpty(block.ToolResult))
        {
            return new Panel(new Markup($"{status} {name}\n[grey]{Escape(block.ToolResult)}[/]"))
            {
                Border = BoxBorder.Rounded
            };
        }

        return new Markup($"{status} {name} {(block.IsComplete ? "" : "[grey]running...[/]")}");
    }

    private void FinalizeCurrentBlock()
    {
        if (_currentBlock != null)
        {
            _currentBlock.IsComplete = true;
            _blocks.Add(_currentBlock);
            _currentBlock = null;
        }
    }

    private void CompleteCurrentBlock()
    {
        if (_currentBlock != null)
        {
            _currentBlock.IsComplete = true;
        }
    }

    private void FinalizeBlocks()
    {
        FinalizeCurrentBlock();
    }

    private static string Escape(string text) =>
        text.Replace("[", "[[").Replace("]", "]]");

    public string GetFullText() => _buffer.ToString();
}

public sealed class StreamBlock
{
    private readonly StringBuilder _text = new();

    public BlockType Type { get; }
    public int Index { get; }
    public string Text => _text.ToString();
    public bool IsComplete { get; set; }
    public string Language { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string ToolStatus { get; set; } = "";
    public string ToolResult { get; set; } = "";

    public StreamBlock(BlockType type, int index)
    {
        Type = type;
        Index = index;
    }

    public void Append(string text) => _text.Append(text);
}

public enum BlockType { Text, Code, Tool, Thinking }

public sealed class MarkdownRenderer
{
    public string Transform(string markdown)
    {
        var result = markdown;

        result = Regex.Replace(result, @"\*\*(.+?)\*\*", "[bold]$1[/]");
        result = Regex.Replace(result, @"\*(.+?)\*", "[italic]$1[/]");
        result = Regex.Replace(result, @"`([^`]+)`", "[yellow]$1[/]");
        result = Regex.Replace(result, @"^### (.+)$", "[bold underline]$1[/]", RegexOptions.Multiline);
        result = Regex.Replace(result, @"^## (.+)$", "[bold cyan]$1[/]", RegexOptions.Multiline);
        result = Regex.Replace(result, @"^# (.+)$", "[bold cyan underline]$1[/]", RegexOptions.Multiline);
        result = Regex.Replace(result, @"^- (.+)$", "  [grey]•[/] $1", RegexOptions.Multiline);
        result = Regex.Replace(result, @"^(\d+)\. (.+)$", "  [grey]$1.[/] $2", RegexOptions.Multiline);

        return result;
    }
}
