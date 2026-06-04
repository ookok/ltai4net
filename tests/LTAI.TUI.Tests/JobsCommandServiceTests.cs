using LTAI.Agent.Tools;
using LTAI.Core.Commands;
using LTAI.TUI.Services;
using Xunit;

namespace LTAI.TUI.Tests;

public sealed class JobsCommandServiceTests
{
    [Fact]
    public void Execute_NullJobs_DoesNotThrow()
    {
        var service = new JobsCommandService(null);
        var cmd = new JobsCommand("list");
        var result = service.Execute(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public void Execute_JobsList_NoJobs_ReturnsMessage()
    {
        using var bgjs = new BackgroundJobService();
        var service = new JobsCommandService(bgjs);
        var cmd = new JobsCommand("list");
        var result = service.Execute(cmd);
        Assert.IsType<SuccessResult>(result);
    }

    [Fact]
    public void Execute_JobsShow_Missing_DoesNotThrow()
    {
        using var bgjs = new BackgroundJobService();
        var service = new JobsCommandService(bgjs);
        var cmd = new JobsCommand("show 999");
        var result = service.Execute(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public void Execute_JobsCancel_Missing_DoesNotThrow()
    {
        using var bgjs = new BackgroundJobService();
        var service = new JobsCommandService(bgjs);
        var cmd = new JobsCommand("cancel 999");
        var result = service.Execute(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public void Execute_JobsWatch_DoesNotThrow()
    {
        var service = new JobsCommandService(null);
        var cmd = new JobsCommand("watch 1");
        var result = service.Execute(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public void Execute_JobsUnknownSubcommand_DoesNotThrow()
    {
        var service = new JobsCommandService(null);
        var cmd = new JobsCommand("unknown_sub");
        var result = service.Execute(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Execute_JobsCancel_CompletedEntry_DoesNotThrow()
    {
        using var bgjs = new BackgroundJobService();
        var jobId = await bgjs.StartJob("echo ok");
        var id = jobId.Split('#')[1].TrimEnd('.');
        // Wait for the job to complete
        Thread.Sleep(500);
        var cmd = new JobsCommand($"cancel {id}");
        var service = new JobsCommandService(bgjs);
        var result = service.Execute(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Execute_JobsShow_AfterStartingJob_DoesNotThrow()
    {
        using var bgjs = new BackgroundJobService();
        var jobId = await bgjs.StartJob("echo hello");
        var id = jobId.Split('#')[1].TrimEnd('.');
        Thread.Sleep(500);
        var service = new JobsCommandService(bgjs);
        var cmd = new JobsCommand($"show {id}");
        var result = service.Execute(cmd);
        Assert.NotNull(result);
    }
}
