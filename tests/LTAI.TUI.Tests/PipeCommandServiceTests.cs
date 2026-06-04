using LTAI.Core.Commands;
using LTAI.TUI.Services;
using Xunit;

namespace LTAI.TUI.Tests;

public sealed class PipeCommandServiceTests
{
    [Fact]
    public void Execute_NullPipesAndRegistry_DoesNotThrow()
    {
        var service = new PipeCommandService(null, null);
        var cmd = new PipeCommand("list");
        var result = service.Execute(cmd);
        Assert.NotNull(result);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("run")]
    [InlineData("stop")]
    public void Execute_PipeSubCommands_DoesNotThrow(string args)
    {
        var service = new PipeCommandService(null, null);
        var cmd = new PipeCommand(args);
        var result = service.Execute(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public void Execute_PipeUnknownSubcommand_DoesNotThrow()
    {
        var service = new PipeCommandService(null, null);
        var cmd = new PipeCommand("unknown_sub");
        var result = service.Execute(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public void Execute_PipeRunWithName_DoesNotThrow()
    {
        var service = new PipeCommandService(null, null);
        var cmd = new PipeCommand("run mypipe");
        var result = service.Execute(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public void Execute_PipeStopWithName_DoesNotThrow()
    {
        var service = new PipeCommandService(null, null);
        var cmd = new PipeCommand("stop mypipe");
        var result = service.Execute(cmd);
        Assert.NotNull(result);
    }
}
