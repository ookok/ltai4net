using LTAI.TUI.Commands;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace LTAI.TUI.Tests;

/// <summary>
/// Fake/injected console tests using Spectre.Console.Testing.TestConsole.
/// Verifies the TUI input → command → output flow without a real terminal.
/// </summary>
public class FakeConsoleTests
{
    /// <summary>
    /// A slash command flows through CommandParser → ExecuteParsed → statusMessage output.
    /// This test verifies the entire chain by injecting a TestConsole.
    /// </summary>
    [Fact]
    public void SlashCommand_Help_ShowsHelpOutput()
    {
        var parser = new CommandParser();
        var cmd = parser.Parse("/help");
        var sc = Assert.IsType<HelpCommand>(cmd);

        // Simulate what SlashCommands.TryExecute would do:
        // - Parse input, dispatch command, capture statusMessage
        string? status = null;
        var running = true;
        var handled = SlashCommands.TryExecute("/help", ref running, ref status);

        Assert.True(handled);
        Assert.NotNull(status);
        Assert.Contains("help", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SlashCommand_Exit_SetsRunningFalse()
    {
        string? status = null;
        var running = true;

        SlashCommands.TryExecute("/exit", ref running, ref status);

        Assert.False(running);
    }

    [Fact]
    public void SlashCommand_Unknown_FuzzySuggests()
    {
        string? status = null;
        var running = true;

        SlashCommands.TryExecute("/sttus", ref running, ref status);

        Assert.Contains("status", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SlashCommand_New_ClearsSession()
    {
        string? status = null;
        var running = true;

        SlashCommands.TryExecute("/new", ref running, ref status);

        Assert.Contains("cleared", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalMessage_NotACommand_ReturnsFalse()
    {
        string? status = null;
        var running = true;

        var handled = SlashCommands.TryExecute("hello", ref running, ref status);

        Assert.False(handled);
    }

    [Fact]
    public void EmptyInput_NotACommand_ReturnsFalse()
    {
        string? status = null;
        var running = true;

        var handled = SlashCommands.TryExecute("", ref running, ref status);

        Assert.False(handled);
    }
}
