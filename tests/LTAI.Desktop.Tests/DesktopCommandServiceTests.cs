using LTAI.Desktop.Services;

namespace LTAI.Desktop.Tests;

public sealed class DesktopCommandServiceTests
{
    private readonly DesktopCommandService _svc = new();

    [Fact]
    public void EmptyInput_ReturnsNull()
    {
        var r = _svc.Execute("");
        Assert.Null(r.StatusMessage);
        Assert.False(r.RequestExit);
        Assert.False(r.ClearMessages);
    }

    [Fact]
    public void WhitespaceInput_ReturnsNull()
    {
        var r = _svc.Execute("   ");
        Assert.Null(r.StatusMessage);
    }

    [Fact]
    public void NonSlashInput_ReturnsNull()
    {
        var r = _svc.Execute("hello");
        Assert.Null(r.StatusMessage);
    }

    [Fact]
    public void UnknownCommand_ShowsWarning()
    {
        var r = _svc.Execute("/xyzzy");
        Assert.NotNull(r.StatusMessage);
        Assert.Contains("未知命令", r.StatusMessage);
    }

    [Fact]
    public void UnknownCommand_WithSuggestion()
    {
        var r = _svc.Execute("/hel");
        Assert.NotNull(r.StatusMessage);
        Assert.Contains("未知命令", r.StatusMessage);
        Assert.Contains("/help", r.StatusMessage);
    }

    [Fact]
    public void Exit_SetsRequestExit()
    {
        var r = _svc.Execute("/exit");
        Assert.True(r.RequestExit);
        Assert.NotNull(r.StatusMessage);
    }

    [Fact]
    public void New_ClearsMessages()
    {
        var r = _svc.Execute("/new");
        Assert.True(r.ClearMessages);
        Assert.NotNull(r.StatusMessage);
    }

    [Fact]
    public void Help_HandledByChatView() // StatusMessage=null means ChatView handles it
    {
        var r = _svc.Execute("/help");
        Assert.Null(r.StatusMessage);
    }

    [Fact]
    public void Status_HandledByChatView()
    {
        var r = _svc.Execute("/status");
        Assert.Null(r.StatusMessage);
    }

    [Fact]
    public void Cost_ReturnsCostSummary()
    {
        var r = _svc.Execute("/cost");
        Assert.NotNull(r.StatusMessage);
    }

    [Fact]
    public void Model_HandledByChatView()
    {
        var r = _svc.Execute("/model");
        Assert.Null(r.StatusMessage);
    }

    [Fact]
    public void Model_WithArgs_HandledByChatView()
    {
        var r = _svc.Execute("/model deepseek");
        Assert.Null(r.StatusMessage);
    }

    [Fact]
    public void Models_HandledByChatView()
    {
        var r = _svc.Execute("/models");
        Assert.Null(r.StatusMessage);
    }

    [Fact]
    public void Config_HandledByChatView()
    {
        var r = _svc.Execute("/config");
        Assert.Null(r.StatusMessage);
    }

    [Fact]
    public void Mode_NoArgs_ShowsUsage()
    {
        var r = _svc.Execute("/mode");
        Assert.NotNull(r.StatusMessage);
        Assert.Contains("用法", r.StatusMessage);
    }

    [Fact]
    public void Mode_WithArgs_Echoes()
    {
        var r = _svc.Execute("/mode review");
        Assert.NotNull(r.StatusMessage);
        Assert.Contains("review", r.StatusMessage);
    }

    [Fact]
    public void Lang_NoArgs_ShowsUsage()
    {
        var r = _svc.Execute("/lang");
        Assert.NotNull(r.StatusMessage);
        Assert.Contains("用法", r.StatusMessage);
    }

    [Fact]
    public void Lang_WithArgs_Echoes()
    {
        var r = _svc.Execute("/lang en");
        Assert.NotNull(r.StatusMessage);
        Assert.Contains("en", r.StatusMessage);
    }

    [Fact]
    public void Undo_ReturnsMessage()
    {
        var r = _svc.Execute("/undo");
        Assert.NotNull(r.StatusMessage);
    }

    [Fact]
    public void Retry_ReturnsMessage()
    {
        var r = _svc.Execute("/retry");
        Assert.NotNull(r.StatusMessage);
    }

    [Fact]
    public void Compact_ReturnsMessage()
    {
        var r = _svc.Execute("/compact");
        Assert.NotNull(r.StatusMessage);
    }

    [Fact]
    public void Pwd_ReturnsDirectory()
    {
        var r = _svc.Execute("/pwd");
        Assert.NotNull(r.StatusMessage);
        Assert.Contains("当前目录", r.StatusMessage);
    }

    [Fact]
    public void Plan_ReturnsMessage()
    {
        var r = _svc.Execute("/plan");
        Assert.NotNull(r.StatusMessage);
    }

    [Fact]
    public void Approve_ReturnsMessage()
    {
        var r = _svc.Execute("/approve");
        Assert.NotNull(r.StatusMessage);
    }

    [Fact]
    public void Ls_ReturnsInfo()
    {
        var r = _svc.Execute("/ls");
        Assert.NotNull(r.StatusMessage);
    }

    [Fact]
    public void Cd_ReturnsMessage()
    {
        var r = _svc.Execute("/cd");
        Assert.NotNull(r.StatusMessage);
    }

    [Fact]
    public void Jobs_ReturnsInfo()
    {
        var r = _svc.Execute("/jobs");
        Assert.NotNull(r.StatusMessage);
        Assert.Contains("作业面板", r.StatusMessage);
    }

    [Fact]
    public void Workflow_ReturnsInfo()
    {
        var r = _svc.Execute("/workflow");
        Assert.NotNull(r.StatusMessage);
        Assert.Contains("工作流面板", r.StatusMessage);
    }

    [Fact]
    public void Pipe_ReturnsMessage()
    {
        var r = _svc.Execute("/pipe");
        Assert.NotNull(r.StatusMessage);
    }

    [Fact]
    public void Skill_ReturnsInfo()
    {
        var r = _svc.Execute("/skill");
        Assert.NotNull(r.StatusMessage);
    }

    [Fact]
    public void Snippet_HandledByChatView()
    {
        var r = _svc.Execute("/snippet");
        Assert.Null(r.StatusMessage);
    }
}
