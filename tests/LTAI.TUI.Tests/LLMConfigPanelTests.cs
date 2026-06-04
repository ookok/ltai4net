using LTAI.Core.Configuration;
using LTAI.TUI;
using Microsoft.Extensions.Options;
using Xunit;

namespace LTAI.TUI.Tests;

public sealed class LLMConfigPanelTests
{
    private static readonly IOptions<LTAIOptions> Options = Microsoft.Extensions.Options.Options.Create(new LTAIOptions());

    [Fact]
    public void Constructor_NullOptions_DoesNotThrow()
    {
        var panel = new LLMConfigPanel();
        Assert.NotNull(panel);
    }

    [Fact]
    public void Constructor_WithOptions_DoesNotThrow()
    {
        var panel = new LLMConfigPanel(Options);
        Assert.NotNull(panel);
    }

    [Fact]
    public void Render_DoesNotThrow()
    {
        var panel = new LLMConfigPanel(Options);
        panel.Render();
        Assert.NotNull(panel);
        // Render outputs via AnsiConsole; no return value to assert.
    }

    [Fact]
    public void HasAnyConfiguredProvider_DoesNotThrow()
    {
        var panel = new LLMConfigPanel(Options);
        // The method checks Environment.GetEnvironmentVariable for known keys.
        // Result depends on actual env vars set on the test machine.
        var result = panel.HasAnyConfiguredProvider();
        // Just verify it returns a bool without throwing
        Assert.IsType<bool>(result);
    }
}
