using LTAI.Core.Commands;
using LTAI.TUI.Services;
using Xunit;

namespace LTAI.TUI.Tests;

public sealed class WorkflowCommandServiceTests
{
    [Fact]
    public async Task Execute_NullRegistry_DoesNotThrow()
    {
        var service = new WorkflowCommandService(null);
        var cmd = new WorkflowCommand("list");
        var result = await service.ExecuteAsync(cmd);
        Assert.NotNull(result);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("reload")]
    [InlineData("show")]
    [InlineData("open")]
    public async Task Execute_WorkflowSubCommands_DoesNotThrow(string args)
    {
        var service = new WorkflowCommandService(null);
        var cmd = new WorkflowCommand(args);
        var result = await service.ExecuteAsync(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Execute_WorkflowUnknownSubcommand_DoesNotThrow()
    {
        var service = new WorkflowCommandService(null);
        var cmd = new WorkflowCommand("unknown_sub");
        var result = await service.ExecuteAsync(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Execute_WorkflowShowWithName_DoesNotThrow()
    {
        var service = new WorkflowCommandService(null);
        var cmd = new WorkflowCommand("show myworkflow");
        var result = await service.ExecuteAsync(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Execute_WorkflowOpenWithName_DoesNotThrow()
    {
        var service = new WorkflowCommandService(null);
        var cmd = new WorkflowCommand("open myworkflow");
        var result = await service.ExecuteAsync(cmd);
        Assert.NotNull(result);
    }
}
