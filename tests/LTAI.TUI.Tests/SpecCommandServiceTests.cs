using LTAI.Core.Commands;
using LTAI.Core.Specs;
using LTAI.TUI.Services;
using Xunit;

namespace LTAI.TUI.Tests;

public sealed class SpecCommandServiceTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly SpecCommandService _service;

    public SpecCommandServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "ltai-spec-test-" + Guid.NewGuid().ToString("n"));
        var specSvc = new SpecService(_tmpDir);
        _service = new SpecCommandService(specSvc);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    [Fact]
    public void Execute_ListEmpty_ReturnsEmptyMessage()
    {
        var cmd = new SpecCommand("");
        var result = _service.Execute(cmd);
        var success = Assert.IsType<SuccessResult>(result);
        Assert.Contains("暂无 spec", success.Markup);
    }

    [Fact]
    public void Execute_NewSpec_CreatesAndLists()
    {
        var create = _service.Execute(new SpecCommand("new test-spec"));
        Assert.IsType<SuccessResult>(create);

        var list = _service.Execute(new SpecCommand("list"));
        var success = Assert.IsType<SuccessResult>(list);
        Assert.Contains("test-spec", success.Markup);
    }

    [Fact]
    public void Execute_NewDuplicate_ReturnsError()
    {
        _service.Execute(new SpecCommand("new dup"));
        var result = _service.Execute(new SpecCommand("new dup"));
        var success = Assert.IsType<SuccessResult>(result);
        Assert.Contains("已存在", success.Markup);
    }

    [Fact]
    public void Execute_ShowExisting_ReturnsContent()
    {
        _service.Execute(new SpecCommand("new showme"));
        var result = _service.Execute(new SpecCommand("show showme"));
        var success = Assert.IsType<SuccessResult>(result);
        Assert.Contains("showme", success.Markup);
    }

    [Fact]
    public void Execute_ShowMissing_ReturnsError()
    {
        var result = _service.Execute(new SpecCommand("show nonexistent"));
        var success = Assert.IsType<SuccessResult>(result);
        Assert.Contains("未找到", success.Markup);
    }

    [Fact]
    public void Execute_DeleteExisting_ReturnsSuccess()
    {
        _service.Execute(new SpecCommand("new to-delete"));
        var result = _service.Execute(new SpecCommand("delete to-delete"));
        Assert.IsType<SuccessResult>(result);

        var list = _service.Execute(new SpecCommand("list"));
        var success = Assert.IsType<SuccessResult>(list);
        Assert.DoesNotContain("to-delete", success.Markup);
    }

    [Fact]
    public void Execute_DeleteMissing_ReturnsError()
    {
        var result = _service.Execute(new SpecCommand("delete ghost"));
        var success = Assert.IsType<SuccessResult>(result);
        Assert.Contains("未找到", success.Markup);
    }

    [Fact]
    public void Execute_PlanWithNoSpec_ReturnsEmpty()
    {
        var result = _service.Execute(new SpecCommand("plan ghost"));
        var success = Assert.IsType<SuccessResult>(result);
        Assert.Contains("尚无 plan", success.Markup);
    }

    [Fact]
    public void Execute_TasksWithNoSpec_ReturnsEmpty()
    {
        var result = _service.Execute(new SpecCommand("tasks ghost"));
        var success = Assert.IsType<SuccessResult>(result);
        Assert.Contains("尚无 task", success.Markup);
    }

    [Fact]
    public void Execute_StatusUpdate_ChangesStatus()
    {
        _service.Execute(new SpecCommand("new statustest"));
        _service.Execute(new SpecCommand("status statustest tasked"));

        var m = new SpecService(_tmpDir).Get("statustest");
        Assert.NotNull(m);
        Assert.Equal(SpecStatus.Tasked, m.Status);
    }

    [Fact]
    public void Execute_UnknownSubcommand_ShowsUsage()
    {
        var result = _service.Execute(new SpecCommand("bogus arg"));
        var success = Assert.IsType<SuccessResult>(result);
        Assert.Contains("用法", success.Markup);
    }
}
