using LTAI.Core.Commands;
using LTAI.TUI.Services;
using Xunit;

namespace LTAI.TUI.Tests;

public sealed class GitCommandServiceTests
{
    private readonly GitCommandService _service = new();

    [Fact]
    public async Task Execute_GitHelp_ReturnsHelpText()
    {
        var cmd = new GitCommand("");
        var result = await _service.ExecuteAsync(cmd);
        var success = Assert.IsType<SuccessResult>(result);
        Assert.Contains("Git 命令", success.Markup);
    }

    [Fact]
    public async Task Execute_GitHelpExplicit_ReturnsHelpText()
    {
        var cmd = new GitCommand("help");
        var result = await _service.ExecuteAsync(cmd);
        var success = Assert.IsType<SuccessResult>(result);
        Assert.Contains("Git 命令", success.Markup);
    }

    [Fact]
    public async Task Execute_NonGitCommand_ReturnsOk()
    {
        var cmd = new LsCommand("");
        var result = await _service.ExecuteAsync(cmd);
        Assert.IsType<SuccessResult>(result);
    }
}
