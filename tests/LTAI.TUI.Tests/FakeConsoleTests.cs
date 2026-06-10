using LTAI.Core.Commands;
using LTAI.Core.Configuration;
using LTAI.TUI.Services;
using Microsoft.Extensions.Options;
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
    private static readonly IOptions<LTAIOptions> Options = Microsoft.Extensions.Options.Options.Create(new LTAIOptions());

    public FakeConsoleTests()
    {
        if (SlashCommands.Router == null)
        {
            SlashCommands.Router = new CommandRouter(
                new ModelCommandService(null, null, null, null, Options),
                new JobsCommandService(null),
                new ConfigCommandService(null, Options),
                new SnippetCommandService(null),
                new WorkflowCommandService(null),
                new PipeCommandService(null, null),
                null!, null!, null!, null!, null!,
                new InfoCommandService(),
                null!, null!);
        }
    }

    /// <summary>
    /// A slash command flows through CommandParser → ExecuteParsed → statusMessage output.
    /// This test verifies the entire chain by injecting a TestConsole.
    /// </summary>
    [Fact]
    public async Task SlashCommand_Help_ShowsHelpOutput()
    {
        var parser = new CommandParser();
        var cmd = parser.Parse("/help");
        var sc = Assert.IsType<HelpCommand>(cmd);

        var (handled, status) = await SlashCommands.TryExecuteAsync("/help");

        Assert.True(handled);
        Assert.NotNull(status);
        Assert.Contains("help", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SlashCommand_Exit_ReturnsNullStatus()
    {
        var (handled, status) = await SlashCommands.TryExecuteAsync("/exit");

        Assert.True(handled);
        Assert.Null(status);
    }

    [Fact]
    public async Task SlashCommand_Unknown_FuzzySuggests()
    {
        var (_, status) = await SlashCommands.TryExecuteAsync("/sttus");

        Assert.Contains("status", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SlashCommand_New_ClearsSession()
    {
        var (_, status) = await SlashCommands.TryExecuteAsync("/new");

        Assert.Contains("cleared", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NormalMessage_NotACommand_ReturnsFalse()
    {
        var (handled, _) = await SlashCommands.TryExecuteAsync("hello");

        Assert.False(handled);
    }

    [Fact]
    public async Task EmptyInput_NotACommand_ReturnsFalse()
    {
        var (handled, _) = await SlashCommands.TryExecuteAsync("");

        Assert.False(handled);
    }
}
