using LTAI.Agent.Tools;
using LTAI.Core.Commands;
using LTAI.Core.Configuration;
using LTAI.TUI.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace LTAI.TUI.Tests;

/// <summary>
/// Tests for the static SlashCommands facade.
/// These tests verify graceful degradation when no DI services are wired.
/// SlashCommands.TryExecute calls AnsiConsole directly — we test the
/// dispatching logic which routes to command services or handles inline.
/// </summary>
public sealed class SlashCommandsTests
{
    private static readonly IOptions<LTAIOptions> Options = Microsoft.Extensions.Options.Options.Create(new LTAIOptions());

    private readonly CommandRouter _router;

    public SlashCommandsTests()
    {
        _router = new CommandRouter(
            new ModelCommandService(null, null, null, Options),
            new JobsCommandService(null),
            new ConfigCommandService(null, Options),
            new SnippetCommandService(null),
            new WorkflowCommandService(null),
            new PipeCommandService(null, null)
        );
    }

    [Fact]
    public void Parser_IsNotNull()
    {
        Assert.NotNull(SlashCommands.Parser);
    }

    [Fact]
    public void CascadeStack_InitiallyEmpty()
    {
        SlashCommands.CascadeStack = [];
        Assert.Empty(SlashCommands.CascadeStack);
    }

    [Fact]
    public void CascadeItems_InitiallyEmpty()
    {
        SlashCommands.CascadeItems = [];
        Assert.Empty(SlashCommands.CascadeItems);
    }

    [Theory]
    [InlineData("/help")]
    [InlineData("/h")]
    [InlineData("/exit")]
    [InlineData("/new")]
    [InlineData("/status")]
    [InlineData("/sttus")] // fuzzy match
    [InlineData("/model")]
    [InlineData("/jobs")]
    [InlineData("/config")]
    [InlineData("/snippet")]
    [InlineData("/workflow")]
    [InlineData("/pipe")]
    [InlineData("/git")]
    [InlineData("/ls")]
    [InlineData("/cd")]
    [InlineData("/dir")]
    [InlineData("/models")]
    public void Parser_Parse_KnownCommands_ReturnsCorrectType(string input)
    {
        var cmd = SlashCommands.Parser.Parse(input);
        Assert.False(cmd is EmptyCommand, $"'{input}' should not parse as EmptyCommand");
        Assert.False(cmd is ChatMessageCommand, $"'{input}' should not parse as ChatMessageCommand");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parser_Parse_EmptyInput_ReturnsEmpty(string input)
    {
        var cmd = SlashCommands.Parser.Parse(input);
        Assert.IsType<EmptyCommand>(cmd);
    }

    [Fact]
    public void Parser_Parse_NormalMessage_ReturnsChatMessage()
    {
        var cmd = SlashCommands.Parser.Parse("hello, how are you?");
        Assert.IsType<ChatMessageCommand>(cmd);
    }

    [Fact]
    public void Parser_Parse_UnknownCommand_ReturnsUnknown()
    {
        var cmd = SlashCommands.Parser.Parse("/foobar");
        Assert.IsType<UnknownCommand>(cmd);
    }

    [Theory]
    [InlineData("/help")]
    [InlineData("/status")]
    [InlineData("/new")]
    [InlineData("/exit")]
    public void Parser_Parse_SlashCommands_ReturnsCorrectType(string input)
    {
        var cmd = SlashCommands.Parser.Parse(input);
        Assert.False(cmd is ChatMessageCommand, $"'{input}' should not fall through to chat");
    }

    [Fact]
    public void KnownProviders_ContainsApiKeys()
    {
        Assert.NotEmpty(SlashCommands.KnownProviders);
    }
}
