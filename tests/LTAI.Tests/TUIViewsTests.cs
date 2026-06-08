using Xunit;
using LTAI.TUI;
using LTAI.TUI.Rendering;
using LTAI.TUI.Services;
using LTAI.TUI.DevUI;
using LTAI.TUI.Input;
using Spectre.Console;

namespace LTAI.Tests;

public class TUIViewsTests
{
    [Fact]
    public void MessagePanelRenderer_BuildWelcomePanel_ReturnsPanel()
    {
        var renderer = new MessagePanelRenderer(AnsiConsole.Console);
        var panel = renderer.BuildWelcomePanel();
        Assert.NotNull(panel);
    }

    [Fact]
    public void MessagePanelRenderer_BuildMessagePanel_User_ReturnsPanel()
    {
        var renderer = new MessagePanelRenderer(AnsiConsole.Console);
        var panel = renderer.BuildMessagePanel("user", "hello world");
        Assert.NotNull(panel);
    }

    [Fact]
    public void MessagePanelRenderer_BuildMessagePanel_Assistant_ReturnsPanel()
    {
        var renderer = new MessagePanelRenderer(AnsiConsole.Console);
        var panel = renderer.BuildMessagePanel("assistant", "```csharp\nvar x = 1;\n```");
        Assert.NotNull(panel);
    }

    [Fact]
    public void MessagePanelRenderer_BuildMessagePanel_Tool_ReturnsPanel()
    {
        var renderer = new MessagePanelRenderer(AnsiConsole.Console);
        var panel = renderer.BuildMessagePanel("tool", "{\"result\": \"ok\"}");
        Assert.NotNull(panel);
    }

    [Fact]
    public void MessagePanelRenderer_BuildCodeBlockPanel_ReturnsPanel()
    {
        var renderer = new MessagePanelRenderer(AnsiConsole.Console);
        var panel = renderer.BuildCodeBlockPanel("var x = 1;", "csharp");
        Assert.NotNull(panel);
    }

    [Fact]
    public void ThemeService_DefaultsToDark_WhenNoEnvVar()
    {
        Assert.False(ThemeService.IsLight);
    }

    [Fact]
    public void ThemeService_Toggle_SwitchesMode()
    {
        var before = ThemeService.IsLight;
        ThemeService.Toggle();
        Assert.NotEqual(before, ThemeService.IsLight);
        ThemeService.Toggle();
        Assert.Equal(before, ThemeService.IsLight);
    }

    [Fact]
    public void CodeBlockBuffer_RegisterAndTryCopyLatest()
    {
        CodeBlockBuffer.Register("code1", "csharp");
        CodeBlockBuffer.Register("code2", "python");
        var result = CodeBlockBuffer.TryCopyLatestToClipboard();
        Assert.True(result);
    }

    [Fact]
    public void SpectreMarkdigRenderer_RendersMarkdown()
    {
        var renderer = new SpectreMarkdigRenderer();
        var md = "# Hello\n**bold** text";
        var result = renderer.RenderToString(md);
        Assert.False(string.IsNullOrEmpty(result));
    }

    [Fact]
    public void SpectreMarkdigRenderer_RendersFencedCodeBlock()
    {
        var renderer = new SpectreMarkdigRenderer();
        var md = "```csharp\nvar x = 1;\n```";
        var result = renderer.RenderToString(md);
        Assert.Contains("var", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SpectreMarkdigRenderer_RendersDiffBlock()
    {
        var renderer = new SpectreMarkdigRenderer();
        var md = "```diff\n+ added line\n- removed line\n```";
        var result = renderer.RenderToString(md);
        Assert.Contains("+", result);
    }

    [Fact]
    public void SpectreMarkdigRenderer_RendersTable()
    {
        var renderer = new SpectreMarkdigRenderer();
        var md = "| A | B |\n|---|---|\n| 1 | 2 |";
        var result = renderer.RenderToString(md);
        Assert.Contains("A", result);
    }

    [Fact]
    public void SpectreMarkdigRenderer_RendersTaskList()
    {
        var renderer = new SpectreMarkdigRenderer();
        var md = "- [x] done\n- [ ] todo";
        var result = renderer.RenderToString(md);
        Assert.False(string.IsNullOrEmpty(result));
    }

    [Fact]
    public void SpectreMarkdigRenderer_RendersStrikethrough()
    {
        var renderer = new SpectreMarkdigRenderer();
        var md = "~~strikethrough~~";
        var result = renderer.RenderToString(md);
        Assert.Contains("strikethrough", result);
    }

    [Fact]
    public void NotificationService_PublishAndConsume()
    {
        NotificationService.Clear();
        NotificationService.Publish("test");
        var consumed = false;
        NotificationService.OnNotification += msg => consumed = true;
        NotificationService.Publish("test2");
        Assert.True(consumed);
        NotificationService.Clear();
    }

    [Fact]
    public async Task SessionsCommandHandler_ListReturnsResults()
    {
        var sessions = new LTAI.Core.Session.SessionManager();
        var handler = new SessionsCommandHandler(sessions);
        var result = await handler.ExecuteAsync("list", () => Task.CompletedTask);
        Assert.NotNull(result);
    }

    [Fact]
    public void SafeWindowHelper_ReturnsPositiveInt()
    {
        Assert.True(SafeWindowHelper.SafeWidth > 0);
        Assert.True(SafeWindowHelper.SafeHeight > 0);
    }
}
