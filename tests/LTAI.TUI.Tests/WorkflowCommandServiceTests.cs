using LTAI.Core.Commands;
using LTAI.TUI.Services;
using Xunit;

namespace LTAI.TUI.Tests;

public sealed class WorkflowCommandServiceTests
{
    [Fact]
    public void Execute_NullRegistry_DoesNotThrow()
    {
        var service = new WorkflowCommandService(null);
        var cmd = new WorkflowCommand("list");
        var result = service.Execute(cmd);
        Assert.NotNull(result);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("reload")]
    [InlineData("show")]
    [InlineData("open")]
    public void Execute_WorkflowSubCommands_DoesNotThrow(string args)
    {
        var service = new WorkflowCommandService(null);
        var cmd = new WorkflowCommand(args);
        var result = service.Execute(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public void Execute_WorkflowUnknownSubcommand_DoesNotThrow()
    {
        var service = new WorkflowCommandService(null);
        var cmd = new WorkflowCommand("unknown_sub");
        var result = service.Execute(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public void Execute_WorkflowShowWithName_DoesNotThrow()
    {
        var service = new WorkflowCommandService(null);
        var cmd = new WorkflowCommand("show myworkflow");
        var result = service.Execute(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public void Execute_WorkflowOpenWithName_DoesNotThrow()
    {
        var service = new WorkflowCommandService(null);
        var cmd = new WorkflowCommand("open myworkflow");
        var result = service.Execute(cmd);
        Assert.NotNull(result);
    }
}
