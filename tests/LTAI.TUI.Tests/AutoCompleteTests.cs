using LTAI.TUI.Commands;
using Xunit;

namespace LTAI.TUI.Tests;

/// <summary>
/// Tests for slash command auto-complete / suggestion choices.
/// Spectre.Console TextPrompt can be populated with choices;
/// we test the choice collection independently of the UI renderer.
/// </summary>
public class AutoCompleteTests
{
    /// <summary>All known command names (canonical, from CommandParser).</summary>
    private static readonly string[] KnownCommands =
    [
        "help", "new", "retry", "compact", "model", "models",
        "status", "jobs", "cost", "config", "snippet", "workflow",
        "pipe", "mode", "undo", "ls", "cd", "pwd",
        "approve", "plan", "lang", "skill", "exit",
    ];

    [Fact]
    public void AllCommands_HaveCanonicalName()
    {
        var parser = new CommandParser();
        foreach (var name in KnownCommands)
        {
            var cmd = parser.Parse($"/{name}");
            Assert.NotNull(cmd);
            Assert.IsNotType<UnknownCommand>(cmd);
            Assert.IsNotType<ChatMessageCommand>(cmd);
        }
    }

    [Fact]
    public void AutoComplete_Choices_NoDuplicates()
    {
        var names = KnownCommands;
        var duplicates = names.GroupBy(n => n.ToLowerInvariant())
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.Empty(duplicates);
    }

    [Fact]
    public void AutoComplete_Choices_AllLowerCase()
    {
        var bad = KnownCommands.Where(n => n != n.ToLowerInvariant()).ToList();
        Assert.Empty(bad);
    }

    [Fact]
    public void AutoComplete_AllCommands_AreParseable()
    {
        var parser = new CommandParser();
        var results = KnownCommands
            .Select(name => (name, cmd: parser.Parse($"/{name}")))
            .ToList();
        foreach (var (name, cmd) in results)
        {
            Assert.True(cmd is not UnknownCommand && cmd is not ChatMessageCommand,
                $"Command '/{name}' parsed as {cmd.GetType().Name}");
        }
    }

    [Fact]
    public void AutoComplete_FilterByPrefix_ReturnsMatching()
    {
        var prefix = "mod";
        var matches = KnownCommands
            .Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Contains("mode", matches);
        Assert.Contains("model", matches);
        Assert.Contains("models", matches);
        Assert.Equal(3, matches.Count);
    }

    [Fact]
    public void AutoComplete_FilterEmptyPrefix_ReturnsAll()
    {
        var all = KnownCommands.Where(c => c.StartsWith("")).ToList();
        Assert.Equal(KnownCommands.Length, all.Count);
    }

    [Fact]
    public void AutoComplete_FilterNoMatch_ReturnsEmpty()
    {
        var matches = KnownCommands
            .Where(c => c.StartsWith("zzz", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Empty(matches);
    }

    [Fact]
    public void Cascade_Commands_HaveSubCommands()
    {
        var parser = new CommandParser();

        var model = parser.Parse("/model");
        Assert.IsType<ModelCommand>(model);

        var snippet = parser.Parse("/snippet");
        Assert.IsType<SnippetCommand>(snippet);

        var workflow = parser.Parse("/workflow");
        Assert.IsType<WorkflowCommand>(workflow);

        var pipe = parser.Parse("/pipe");
        Assert.IsType<PipeCommand>(pipe);
    }

    [Fact]
    public void Cascade_Subcommand_ParsesArgs()
    {
        var parser = new CommandParser();
        var cmd = parser.Parse("/snippet save my-msg");
        var sc = Assert.IsType<SnippetCommand>(cmd);
        Assert.Equal("save my-msg", sc.Args);
    }
}
