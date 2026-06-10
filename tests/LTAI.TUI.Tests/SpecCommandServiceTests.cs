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
    public async Task Execute_ListEmpty_ReturnsEmptyMessage()
    {
        var cmd = new SpecCommand("");
        var result = await _service.ExecuteAsync(cmd);
        var success = Assert.IsType<SuccessResult>(result);
        Assert.Contains("暂无 spec", success.Markup);
    }

    [Fact]
    public async Task Execute_NewSpec_CreatesAndLists()
    {
        var create = await _service.ExecuteAsync(new SpecCommand("new test-spec"));
        Assert.IsType<SuccessResult>(create);

        var list = await _service.ExecuteAsync(new SpecCommand("list"));
        var success = Assert.IsType<SuccessResult>(list);
        Assert.Contains("test-spec", success.Markup);
    }

    [Fact]
    public async Task Execute_NewDuplicate_ReturnsError()
    {
        await _service.ExecuteAsync(new SpecCommand("new dup"));
        var result = await _service.ExecuteAsync(new SpecCommand("new dup"));
        var success = Assert.IsType<SuccessResult>(result);
        Assert.Contains("已存在", success.Markup);
    }

    [Fact]
    public async Task Execute_ShowExisting_ReturnsContent()
    {
        await _service.ExecuteAsync(new SpecCommand("new showme"));
        var result = await _service.ExecuteAsync(new SpecCommand("show showme"));
        var success = Assert.IsType<SuccessResult>(result);
        Assert.Contains("showme", success.Markup);
    }

    [Fact]
    public async Task Execute_ShowMissing_ReturnsError()
    {
        var result = await _service.ExecuteAsync(new SpecCommand("show nonexistent"));
        var success = Assert.IsType<SuccessResult>(result);
        Assert.Contains("未找到", success.Markup);
    }

    [Fact]
    public async Task Execute_DeleteExisting_ReturnsSuccess()
    {
        await _service.ExecuteAsync(new SpecCommand("new to-delete"));
        var result = await _service.ExecuteAsync(new SpecCommand("delete to-delete"));
        Assert.IsType<SuccessResult>(result);

        var list = await _service.ExecuteAsync(new SpecCommand("list"));
        var success = Assert.IsType<SuccessResult>(list);
        Assert.DoesNotContain("to-delete", success.Markup);
    }

    [Fact]
    public async Task Execute_DeleteMissing_ReturnsError()
    {
        var result = await _service.ExecuteAsync(new SpecCommand("delete ghost"));
        var success = Assert.IsType<SuccessResult>(result);
        Assert.Contains("未找到", success.Markup);
    }

    [Fact]
    public async Task Execute_PlanWithNoSpec_ReturnsEmpty()
    {
        var result = await _service.ExecuteAsync(new SpecCommand("plan ghost"));
        var success = Assert.IsType<SuccessResult>(result);
        Assert.Contains("尚无 plan", success.Markup);
    }

    [Fact]
    public async Task Execute_TasksWithNoSpec_ReturnsEmpty()
    {
        var result = await _service.ExecuteAsync(new SpecCommand("tasks ghost"));
        var success = Assert.IsType<SuccessResult>(result);
        Assert.Contains("尚无 task", success.Markup);
    }

    [Fact]
    public async Task Execute_StatusUpdate_ChangesStatus()
    {
        await _service.ExecuteAsync(new SpecCommand("new statustest"));
        await _service.ExecuteAsync(new SpecCommand("status statustest tasked"));

        var m = new SpecService(_tmpDir).Get("statustest");
        Assert.NotNull(m);
        Assert.Equal(SpecStatus.Tasked, m.Status);
    }

    [Fact]
    public async Task Execute_UnknownSubcommand_ShowsUsage()
    {
        var result = await _service.ExecuteAsync(new SpecCommand("bogus arg"));
        var success = Assert.IsType<SuccessResult>(result);
        Assert.Contains("用法", success.Markup);
    }
}
