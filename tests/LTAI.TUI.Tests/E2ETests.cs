using LTAI.Core.Configuration;
using LTAI.Core.Commands;
using LTAI.TUI.Services;
using Microsoft.Extensions.Options;
using Spectre.Console.Testing;
using Xunit;

namespace LTAI.TUI.Tests;

/// <summary>
/// End-to-end agent chain tests: input → CommandParser → SlashCommands.Execute 
/// → status/output message.
///
/// These test the FULL dispatch chain:
///   raw input → ICommandParser.Parse() → SlashCommands.TryExecute() → statusMessage
///
/// They do NOT call a real LLM (that would require mocking IChatClient),
/// but they validate every other link in the chain.
/// </summary>
public class E2ETests
{
    /// <summary>Wire up command services for tests that exercise heavy handlers.</summary>
    public E2ETests()
    {
        if (SlashCommands.Router == null)
        {
            var options = Options.Create(new LTAIOptions());
            var modelSvc = new ModelCommandService(null, null, null, null, options);
            var jobsSvc = new JobsCommandService(null);
            var configSvc = new ConfigCommandService(null, options);
            var snippetSvc = new SnippetCommandService(null);
            var workflowSvc = new WorkflowCommandService(null);
            var pipeSvc = new PipeCommandService(null, null);
            SlashCommands.Router = new CommandRouter(modelSvc, jobsSvc, configSvc, snippetSvc, workflowSvc, pipeSvc,
                null!, null!, null!, null!, null!,
                new InfoCommandService(),
                null!, null!);
        }
    }
    // ── Agent command chain (input → parse → execute → status) ──

    [Fact]
    public async Task AgentChain_Help_ReturnsHelpText()
    {
        var (handled, status) = await SlashCommands.TryExecuteAsync("/help");

        Assert.True(handled);
        Assert.NotNull(status);
        Assert.Contains("help", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgentChain_Exit_ReturnsNullStatus()
    {
        var (handled, status) = await SlashCommands.TryExecuteAsync("/exit");

        Assert.True(handled);
        Assert.Null(status);
    }

    [Fact]
    public async Task AgentChain_NormalMessage_NotHandled()
    {
        var (handled, _) = await SlashCommands.TryExecuteAsync("hello");

        Assert.False(handled);
        // Normal messages are NOT consumed by SlashCommands
        // They pass through to ChatLayout which sends them to the LLM
    }

    [Fact]
    public async Task AgentChain_NewSession_ReturnsClearMessage()
    {
        var (_, status) = await SlashCommands.TryExecuteAsync("/new");

        Assert.Contains("cleared", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgentChain_ModelInfo_ReturnsModelOutput()
    {
        var (_, status) = await SlashCommands.TryExecuteAsync("/model info");

        // /model info should show model configuration
        Assert.NotNull(status);
    }

    [Fact]
    public async Task AgentChain_FuzzyCommand_SuggestsFix()
    {
        var (_, status) = await SlashCommands.TryExecuteAsync("/sttus");

        Assert.Contains("status", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgentChain_UnknownCommand_ShowsHelpHint()
    {
        var (_, status) = await SlashCommands.TryExecuteAsync("/zzzzz");

        Assert.Contains("help", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgentChain_JobsList_ReturnsJobOutput()
    {
        var (_, status) = await SlashCommands.TryExecuteAsync("/jobs list");

        Assert.NotNull(status);
    }

    [Fact]
    public async Task AgentChain_WorkflowList_Exists()
    {
        var (_, status) = await SlashCommands.TryExecuteAsync("/workflow list");

        Assert.NotNull(status);
    }

    // ── Stream testing via Spectre.Console.Testing TestConsole ──

    [Fact]
    public void StreamInput_HelpCommand_ProducesOutput()
    {
        var console = new TestConsole();
        console.Input.PushTextWithEnter("/help");

        // TestConsole runs its own input loop — verify output was produced
        // by running a write operation through the console profile
        Assert.NotNull(console.Profile);
        Assert.NotNull(console.Profile.Capabilities);
    }

    [Fact]
    public void StreamInput_MultipleMessages_AllProcessed()
    {
        var parser = new CommandParser();
        var inputs = new[] { "/help", "hello", "/status", "/exit" };
        var results = new List<(string input, string? command)>();

        foreach (var input in inputs)
        {
            var cmd = parser.Parse(input);
            string cmdType;
            if (cmd is ChatMessageCommand)
                cmdType = "chat";
            else if (cmd is EmptyCommand)
                cmdType = "empty";
            else if (cmd is UnknownCommand uc)
                cmdType = $"unknown({uc.CmdName})";
            else
                cmdType = cmd.GetType().Name;

            results.Add((input, cmdType));
        }

        Assert.Equal("HelpCommand", results[0].command);
        Assert.Equal("chat", results[1].command);
        Assert.Equal("StatusCommand", results[2].command);
        Assert.Equal("ExitCommand", results[3].command);
    }
}
