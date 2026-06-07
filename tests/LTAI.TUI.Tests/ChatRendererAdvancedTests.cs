using LTAI.TUI.Rendering;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using Xunit;

namespace LTAI.TUI.Tests;

public sealed class ChatRendererAdvancedTests
{
    private readonly TestConsole _console = new();
    private readonly ChatRenderer _renderer;

    public ChatRendererAdvancedTests()
    {
        _renderer = new ChatRenderer(_console);
    }

    [Fact]
    public void BuildMessagesPanel_WithScrollOffset_ShowsScrollIndicator()
    {
        var history = new List<(string, IRenderable?, string, string?)>();
        for (int i = 0; i < 20; i++)
            history.Add(("user", null, $"msg{i}", null));
        var result = _renderer.BuildMessagesPanel(history, null, null, 5, 10, null);
        Assert.NotNull(result);
    }

    [Fact]
    public void BuildMessagesPanel_WithToolCalls_DoesNotThrow()
    {
        var history = new List<(string, IRenderable?, string, string?)>();
        var toolCalls = new List<(string name, string args, string result)>
        {
            ("read_file", "path=test.cs", "file content"),
            ("write_file", "path=out.cs", "written"),
        };
        var result = _renderer.BuildMessagesPanel(history, "streaming", toolCalls, 0, 50, null);
        Assert.NotNull(result);
    }

    [Fact]
    public void BuildFooter_StartupMessage_ShowsMessage()
    {
        var footer = _renderer.BuildFooter("", "", false, new List<string> { "" }, 0, 0, 5,
            null, 0, "请配置 L1 模型");
        Assert.NotNull(footer);
    }

    [Fact]
    public void HighlightCommands_HighlightsSlashAndHash()
    {
        var result = _renderer.HighlightCommands("/help /model #todo");
        Assert.NotNull(result);
        Assert.Contains("/help", result);
        Assert.Contains("#todo", result);
    }

    [Fact]
    public void MdToPanelContent_CodeFence_ReturnsFormatted()
    {
        var result = _renderer.MdToPanelContent("```csharp\nvar x = 1;\n```");
        Assert.NotNull(result);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void MdToPanelContent_Table_DoesNotThrow()
    {
        var md = "| H1 | H2 |\n|----|----|\n| A  | B  |";
        var result = _renderer.MdToPanelContent(md);
        Assert.NotNull(result);
    }

    [Fact]
    public void MdToPanelContent_UnclosedFence_DoesNotThrow()
    {
        var result = _renderer.MdToPanelContent("```python\nprint('hello')");
        Assert.NotNull(result);
    }

    [Fact]
    public void RenderToolCallsAsTree_MultipleCalls_ReturnsTree()
    {
        var calls = new List<(string, string, string)>
        {
            ("search", "query=test", "result1"),
            ("read", "path=file.txt", "result2"),
        };
        var tree = _renderer.RenderToolCallsAsTree(calls);
        Assert.Contains("search", tree);
        Assert.Contains("read", tree);
    }
}
