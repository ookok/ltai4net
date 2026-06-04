using LTAI.TUI.Commands;
using Xunit;

namespace LTAI.TUI.Tests;

public class CommandParserTests
{
    private readonly ICommandParser _parser = new CommandParser();

    // ── Simple commands (no args) ──

    [Fact]
    public void Parse_Help_ReturnsHelpCommand()
    {
        var cmd = _parser.Parse("/help");
        Assert.IsType<HelpCommand>(cmd);
    }

    [Fact]
    public void Parse_HelpAlias_ReturnsHelpCommand()
    {
        var cmd = _parser.Parse("/?");
        Assert.IsType<HelpCommand>(cmd);
    }

    [Fact]
    public void Parse_Exit_ReturnsExitCommand()
    {
        var cmd = _parser.Parse("/exit");
        Assert.IsType<ExitCommand>(cmd);
    }

    [Fact]
    public void Parse_New_ReturnsNewSessionCommand()
    {
        var cmd = _parser.Parse("/new");
        Assert.IsType<NewSessionCommand>(cmd);
    }

    [Fact]
    public void Parse_NewAlias_ReturnsNewSessionCommand()
    {
        var cmd = _parser.Parse("/clear");
        Assert.IsType<NewSessionCommand>(cmd);
    }

    [Fact]
    public void Parse_Status_ReturnsStatusCommand()
    {
        var cmd = _parser.Parse("/status");
        Assert.IsType<StatusCommand>(cmd);
    }

    [Fact]
    public void Parse_Models_ReturnsModelsCommand()
    {
        var cmd = _parser.Parse("/models");
        Assert.IsType<ModelsCommand>(cmd);
    }

    // ── Commands with args ──

    [Fact]
    public void Parse_ModelWithArgs_ReturnsModelCommand()
    {
        var cmd = _parser.Parse("/model info");
        var mc = Assert.IsType<ModelCommand>(cmd);
        Assert.Equal("info", mc.Args);
    }

    [Fact]
    public void Parse_ModelNoArgs_ReturnsModelCommand()
    {
        var cmd = _parser.Parse("/model");
        Assert.IsType<ModelCommand>(cmd);
    }

    [Fact]
    public void Parse_JobsWithArgs_ReturnsJobsCommand()
    {
        var cmd = _parser.Parse("/jobs list");
        var jc = Assert.IsType<JobsCommand>(cmd);
        Assert.Equal("list", jc.Args);
    }

    [Fact]
    public void Parse_ConfigWithArgs_ReturnsConfigCommand()
    {
        var cmd = _parser.Parse("/config export");
        var cc = Assert.IsType<ConfigCommand>(cmd);
        Assert.Equal("export", cc.Args);
    }

    [Fact]
    public void Parse_WorkflowWithArgs_ReturnsWorkflowCommand()
    {
        var cmd = _parser.Parse("/workflow list");
        var wc = Assert.IsType<WorkflowCommand>(cmd);
        Assert.Equal("list", wc.Args);
    }

    [Fact]
    public void Parse_PipeWithArgs_ReturnsPipeCommand()
    {
        var cmd = _parser.Parse("/pipe run test");
        var pc = Assert.IsType<PipeCommand>(cmd);
        Assert.Equal("run test", pc.Args);
    }

    [Fact]
    public void Parse_SnippetWithArgs_ReturnsSnippetCommand()
    {
        var cmd = _parser.Parse("/snippet save hello");
        var sc = Assert.IsType<SnippetCommand>(cmd);
        Assert.Equal("save hello", sc.Args);
    }

    [Fact]
    public void Parse_LsWithArgs_ReturnsLsCommand()
    {
        var cmd = _parser.Parse("/ls src");
        var lc = Assert.IsType<LsCommand>(cmd);
        Assert.Equal("src", lc.Args);
    }

    [Fact]
    public void Parse_CdWithArgs_ReturnsCdCommand()
    {
        var cmd = _parser.Parse("/cd /tmp");
        var cc = Assert.IsType<CdCommand>(cmd);
        Assert.Equal("/tmp", cc.Args);
    }

    [Fact]
    public void Parse_LangWithArgs_ReturnsLangCommand()
    {
        var cmd = _parser.Parse("/lang zh-CN");
        var lc = Assert.IsType<LangCommand>(cmd);
        Assert.Equal("zh-CN", lc.Args);
    }

    // ── Aliases ──

    [Fact]
    public void Parse_AliasChineseHelp_ReturnsHelpCommand()
    {
        var cmd = _parser.Parse("/帮助");
        Assert.IsType<HelpCommand>(cmd);
    }

    [Fact]
    public void Parse_AliasChineseNew_ReturnsNewSessionCommand()
    {
        var cmd = _parser.Parse("/重置");
        // Verify via the alias mapping — "/重置" should map to "new" -> NewSessionCommand
        var result = _parser.Parse("/重置");
        Assert.IsType<NewSessionCommand>(result);
    }

    [Fact]
    public void Parse_AliasChineseExit_ReturnsExitCommand()
    {
        var cmd = _parser.Parse("/退出");
        Assert.IsType<ExitCommand>(cmd);
    }

    // ── Edge cases ──

    [Fact]
    public void Parse_EmptyString_ReturnsEmptyCommand()
    {
        var cmd = _parser.Parse("");
        Assert.IsType<EmptyCommand>(cmd);
    }

    [Fact]
    public void Parse_WhitespaceString_ReturnsEmptyCommand()
    {
        var cmd = _parser.Parse("   ");
        Assert.IsType<EmptyCommand>(cmd);
    }

    [Fact]
    public void Parse_NormalMessage_ReturnsChatMessageCommand()
    {
        var cmd = _parser.Parse("hello, what is AI?");
        var cm = Assert.IsType<ChatMessageCommand>(cmd);
        Assert.Equal("hello, what is AI?", cm.Text);
    }

    [Fact]
    public void Parse_JustSlash_ReturnsHelpCommand()
    {
        var cmd = _parser.Parse("/");
        Assert.IsType<HelpCommand>(cmd);
    }

    [Fact]
    public void Parse_UnknownCommand_ReturnsUnknownCommand()
    {
        var cmd = _parser.Parse("/foobar");
        var uc = Assert.IsType<UnknownCommand>(cmd);
        Assert.Equal("foobar", uc.CmdName);
    }

    [Fact]
    public void Parse_FuzzyMatch_ReturnsUnknownWithSuggestion()
    {
        // "sttus" is Levenshtein distance 1 from "status"
        var cmd = _parser.Parse("/sttus");
        var uc = Assert.IsType<UnknownCommand>(cmd);
        Assert.NotNull(uc.Suggestion);
        // Suggestion should be "status" (the canonical name)
        Assert.Contains("status", uc.Suggestion, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_FuzzyMatchAlias_ReturnsUnknownWithSuggestion()
    {
        // "stat" is Levenshtein distance 2 from "status"
        var cmd = _parser.Parse("/stat");
        var uc = Assert.IsType<UnknownCommand>(cmd);
        Assert.NotNull(uc.Suggestion);
    }

    [Fact]
    public void Parse_FarFromAnyCommand_ReturnsUnknownNoSuggestion()
    {
        // "zzzzz" is Levenshtein distance > 3 from any command
        var cmd = _parser.Parse("/zzzzz");
        var uc = Assert.IsType<UnknownCommand>(cmd);
        Assert.Null(uc.Suggestion);
    }

    [Fact]
    public void Parse_SlashWithSpaces_WorksCorrectly()
    {
        var cmd = _parser.Parse("  /help  ");
        Assert.IsType<HelpCommand>(cmd);
    }

    [Fact]
    public void Parse_KebabArgs_WorksCorrectly()
    {
        var cmd = _parser.Parse("/workflow show decision-tree");
        var wc = Assert.IsType<WorkflowCommand>(cmd);
        Assert.Equal("show decision-tree", wc.Args);
    }

    // ── Multi-word messages ──

    [Fact]
    public void Parse_MultiWordQuestion_ReturnsChatMessageCommand()
    {
        var cmd = _parser.Parse("how do I implement a singleton pattern in C#?");
        var cm = Assert.IsType<ChatMessageCommand>(cmd);
        Assert.Contains("singleton", cm.Text);
    }

    [Fact]
    public void Parse_JustNumbers_ReturnsChatMessageCommand()
    {
        var cmd = _parser.Parse("42");
        Assert.IsType<ChatMessageCommand>(cmd);
    }

    // ── Code detection ──

    [Fact]
    public void Parse_CodeSnippet_ReturnsChatMessageCommand()
    {
        var cmd = _parser.Parse("public class Foo { }");
        Assert.IsType<ChatMessageCommand>(cmd);
    }
}
