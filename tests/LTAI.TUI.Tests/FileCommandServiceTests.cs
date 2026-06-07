using LTAI.Core.Commands;
using LTAI.TUI.Services;
using Xunit;

namespace LTAI.TUI.Tests;

public sealed class FileCommandServiceTests
{
    private readonly FileCommandService _service = new();

    [Fact]
    public void Execute_PwdCommand_ReturnsCurrentDir()
    {
        var cmd = new PwdCommand();
        var result = _service.Execute(cmd);
        var success = Assert.IsType<SuccessResult>(result);
        Assert.Contains(Directory.GetCurrentDirectory(), success.Markup);
    }

    [Fact]
    public void Execute_CdNoArgs_ReturnsCurrentDir()
    {
        var cmd = new CdCommand("");
        var result = _service.Execute(cmd);
        var success = Assert.IsType<SuccessResult>(result);
        Assert.Contains("当前目录", success.Markup);
    }

    [Fact]
    public void Execute_CdToNonExistent_ReturnsError()
    {
        var cmd = new CdCommand("__nonexistent_dir_12345__");
        var result = _service.Execute(cmd);
        var success = Assert.IsType<SuccessResult>(result);
        Assert.Contains("目录不存在", success.Markup);
    }

    [Fact]
    public void Execute_LsCommand_ReturnsResult()
    {
        var cmd = new LsCommand("");
        var result = _service.Execute(cmd);
        Assert.IsType<SuccessResult>(result);
    }

    [Fact]
    public void Execute_NonFileCommand_ReturnsOk()
    {
        var cmd = new HelpCommand();
        var result = _service.Execute(cmd);
        Assert.IsType<SuccessResult>(result);
    }
}
