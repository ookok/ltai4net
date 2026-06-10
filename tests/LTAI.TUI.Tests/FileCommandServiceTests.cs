using LTAI.Core.Commands;
using LTAI.TUI.Services;
using Xunit;

namespace LTAI.TUI.Tests;

public sealed class FileCommandServiceTests
{
    private readonly FileCommandService _service = new();

    [Fact]
    public async Task Execute_PwdCommand_ReturnsCurrentDir()
    {
        var cmd = new PwdCommand();
        var result = await _service.ExecuteAsync(cmd);
        var success = Assert.IsType<SuccessResult>(result);
        Assert.Contains(Directory.GetCurrentDirectory(), success.Markup);
    }

    [Fact]
    public async Task Execute_CdNoArgs_ReturnsCurrentDir()
    {
        var cmd = new CdCommand("");
        var result = await _service.ExecuteAsync(cmd);
        var success = Assert.IsType<SuccessResult>(result);
        Assert.Contains("当前目录", success.Markup);
    }

    [Fact]
    public async Task Execute_CdToNonExistent_ReturnsError()
    {
        var cmd = new CdCommand("__nonexistent_dir_12345__");
        var result = await _service.ExecuteAsync(cmd);
        var success = Assert.IsType<SuccessResult>(result);
        Assert.Contains("目录不存在", success.Markup);
    }

    [Fact]
    public async Task Execute_LsCommand_ReturnsResult()
    {
        var cmd = new LsCommand("");
        var result = await _service.ExecuteAsync(cmd);
        Assert.IsType<SuccessResult>(result);
    }

    [Fact]
    public async Task Execute_NonFileCommand_ReturnsOk()
    {
        var cmd = new HelpCommand();
        var result = await _service.ExecuteAsync(cmd);
        Assert.IsType<SuccessResult>(result);
    }
}
