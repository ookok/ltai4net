using LTAI.Core.Commands;
using LTAI.TUI.Services;
using Xunit;

namespace LTAI.TUI.Tests;

public sealed class PipeCommandServiceTests
{
    [Fact]
    public async Task Execute_NullPipesAndRegistry_DoesNotThrow()
    {
        var service = new PipeCommandService(null, null);
        var cmd = new PipeCommand("list");
        var result = await service.ExecuteAsync(cmd);
        Assert.NotNull(result);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("run")]
    [InlineData("stop")]
    public async Task Execute_PipeSubCommands_DoesNotThrow(string args)
    {
        var service = new PipeCommandService(null, null);
        var cmd = new PipeCommand(args);
        var result = await service.ExecuteAsync(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Execute_PipeUnknownSubcommand_DoesNotThrow()
    {
        var service = new PipeCommandService(null, null);
        var cmd = new PipeCommand("unknown_sub");
        var result = await service.ExecuteAsync(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Execute_PipeRunWithName_DoesNotThrow()
    {
        var service = new PipeCommandService(null, null);
        var cmd = new PipeCommand("run mypipe");
        var result = await service.ExecuteAsync(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Execute_PipeStopWithName_DoesNotThrow()
    {
        var service = new PipeCommandService(null, null);
        var cmd = new PipeCommand("stop mypipe");
        var result = await service.ExecuteAsync(cmd);
        Assert.NotNull(result);
    }
}
