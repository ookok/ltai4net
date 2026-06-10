using LTAI.TUI.Rendering;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using Xunit;

namespace LTAI.TUI.Tests;

public sealed class ChatRendererTests
{
    private readonly TestConsole _console = new();
    private readonly ChatRenderer _renderer;

    public ChatRendererTests()
    {
        _renderer = new ChatRenderer(_console);
    }

    [Theory]
    [InlineData("user", "hello")]
    [InlineData("assistant", "world")]
    [InlineData("tool", "result")]
    [InlineData("error", "ERR")]
    [InlineData("cmd", "ls -la")]
    [InlineData("system", "init")]
    [InlineData("unknown", "???")]
    public void BuildMessagePanel_DoesNotThrow(string role, string content)
    {
        var panel = _renderer.BuildMessagePanel(role, content, 0, null, null);
        Assert.NotNull(panel);
    }

    [Fact]
    public void BuildMessagePanel_WithExpandedMessages_DoesNotThrow()
    {
        var panel = _renderer.BuildMessagePanel("user", "hello", 0, null, new HashSet<int> { 0 });
        Assert.NotNull(panel);
    }

    [Fact]
    public void BuildMessagePanel_WithReasoning_DoesNotThrow()
    {
        var panel = _renderer.BuildMessagePanel("assistant", "ans", 0, "I'm thinking", null);
        Assert.NotNull(panel);
    }

    [Fact]
    public void BuildCodeBlockPanel_ReturnsPanel()
    {
        var panel = _renderer.BuildCodeBlockPanel("var x = 1;", "javascript");
        Assert.NotNull(panel);
    }

    [Fact]
    public void BuildCodeBlockPanel_NullLanguage_DoesNotThrow()
    {
        var panel = _renderer.BuildCodeBlockPanel("code", null);
        Assert.NotNull(panel);
    }

    [Fact]
    public void BuildMessagesPanel_EmptyHistory_ReturnsWelcome()
    {
        var history = new List<(string, IRenderable?, string, string?)>();
        var result = _renderer.BuildMessagesPanel(history, null, null, 0, 50, null);
        Assert.NotNull(result);
    }

    [Fact]
    public void BuildMessagesPanel_WithRenderedHistory_DoesNotThrow()
    {
        var panel = _renderer.BuildMessagePanel("user", "hi", 0, null, null);
        var history = new List<(string, IRenderable?, string, string?)>
        {
            ("user", panel, "hi", null)
        };
        var result = _renderer.BuildMessagesPanel(history, null, null, 0, 50, null);
        Assert.NotNull(result);
    }

    [Fact]
    public void BuildMessagesPanel_WithStreamingContent_DoesNotThrow()
    {
        var history = new List<(string, IRenderable?, string, string?)>();
        var result = _renderer.BuildMessagesPanel(history, "streaming...", [], 0, 50, null);
        Assert.NotNull(result);
    }

    [Fact]
    public void BuildWelcomePanel_DoesNotThrow()
    {
        var panel = _renderer.BuildWelcomePanel();
        Assert.NotNull(panel);
    }

    [Fact]
    public void BuildFooter_DoesNotThrow()
    {
        var footer = _renderer.BuildFooter("", "", false, new List<string> { "" }, 0, 0, 10, null, 0);
        Assert.NotNull(footer);
    }

    [Fact]
    public void BuildFooter_WithSuggestions_DoesNotThrow()
    {
        var suggestions = new List<SlashCommands.SuggestionItem>
        {
            new("/help", "/help", "commands", false),
            new("/new", "/new", "commands", false),
            new("/status", "/status", "commands", false),
        };
        var footer = _renderer.BuildFooter("/h", "ok", false, new List<string> { "/h" }, 0, 2, 10, suggestions, 1);
        Assert.NotNull(footer);
    }

    [Fact]
    public void BuildFooter_MultiLineInput_DoesNotThrow()
    {
        var lines = new List<string> { "line1", "line2", "line3" };
        var footer = _renderer.BuildFooter("", "", false, lines, 1, 3, 10, null, 0);
        Assert.NotNull(footer);
    }

    [Fact]
    public void MdToPanelContent_VariousInputs_DoesNotThrow()
    {
        var result = ChatRenderer.MdToPanelContent("Hello **world**");
        Assert.NotNull(result);
        result = ChatRenderer.MdToPanelContent("Normal text");
        Assert.NotNull(result);
    }

    [Fact]
    public void MdToPanelContent_EmptyString_DoesNotThrow()
    {
        var result = ChatRenderer.MdToPanelContent("");
        Assert.NotNull(result);
    }

    [Fact]
    public void RenderToolCallsAsTree_Empty_DoesNotThrow()
    {
        var tree = _renderer.RenderToolCallsAsTree([]);
        Assert.NotNull(tree);
    }

    [Fact]
    public void HighlightCommands_DoesNotThrow()
    {
        var result = ChatRenderer.HighlightCommands("/help /model info #tag");
        Assert.NotNull(result);
    }

    [Fact]
    public void PulseFrames_NotEmpty()
    {
        Assert.NotEmpty(ChatRenderer.PulseFrames);
    }
}
