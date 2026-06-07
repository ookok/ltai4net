using LTAI.Core.Commands;
using LTAI.TUI.Services;
using Xunit;

namespace LTAI.TUI.Tests;

public sealed class GitCommandServiceTests
{
    private readonly GitCommandService _service = new();

    [Fact]
    public void Execute_GitHelp_ReturnsHelpText()
    {
        var cmd = new GitCommand("");
        var result = _service.Execute(cmd);
        var success = Assert.IsType<SuccessResult>(result);
        Assert.Contains("Git 命令", success.Markup);
    }

    [Fact]
    public void Execute_GitHelpExplicit_ReturnsHelpText()
    {
        var cmd = new GitCommand("help");
        var result = _service.Execute(cmd);
        var success = Assert.IsType<SuccessResult>(result);
        Assert.Contains("Git 命令", success.Markup);
    }

    [Fact]
    public void Execute_NonGitCommand_ReturnsOk()
    {
        var cmd = new LsCommand("");
        var result = _service.Execute(cmd);
        Assert.IsType<SuccessResult>(result);
    }
}
