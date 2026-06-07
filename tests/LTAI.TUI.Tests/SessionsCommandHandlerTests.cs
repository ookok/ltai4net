using LTAI.Core.Session;
using Microsoft.Extensions.AI;
using Xunit;

namespace LTAI.TUI.Tests;

public sealed class SessionsCommandHandlerTests : IDisposable
{
    private readonly SessionManager _sessions;
    private readonly SessionsCommandHandler _handler;
    private readonly string _sessionsDir;
    private int _saveCount;

    public SessionsCommandHandlerTests()
    {
        _sessionsDir = Path.Combine(Path.GetTempPath(), "ltai-session-test-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_sessionsDir);
        _sessions = new SessionManager(_sessionsDir);
        _handler = new SessionsCommandHandler(_sessions);
        _handler.NewSession();
    }

    public void Dispose()
    {
        try { Directory.Delete(_sessionsDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Execute_ListWhenEmpty_ReturnsNoSessions()
    {
        var result = await _handler.ExecuteAsync("/sessions list", () => Task.CompletedTask);
        Assert.NotEmpty(result.HistoryMessages);
        Assert.Contains("暂无", string.Join("", result.HistoryMessages));
    }

    [Fact]
    public async Task Execute_ListAfterCreate_ReturnsSession()
    {
        _handler.NewSession();
        var result = await _handler.ExecuteAsync("/sessions list", () => Task.CompletedTask);
        Assert.NotEmpty(result.HistoryMessages);
    }

    [Fact]
    public async Task Execute_LoadWithNoArg_ReturnsError()
    {
        var result = await _handler.ExecuteAsync("/sessions load", () => Task.CompletedTask);
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Execute_LoadNonExistent_ReturnsError()
    {
        var result = await _handler.ExecuteAsync("/sessions load ghost", () => Task.CompletedTask);
        Assert.True(result.IsError);
        Assert.Contains("找不到", string.Join("", result.HistoryMessages));
    }

    [Fact]
    public async Task Execute_DeleteWithNoArg_ReturnsError()
    {
        var result = await _handler.ExecuteAsync("/sessions delete", () => Task.CompletedTask);
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Execute_DeleteNonExistent_DoesNotThrow()
    {
        var result = await _handler.ExecuteAsync("/sessions delete ghost", () => Task.CompletedTask);
        Assert.Null(result.LoadedMessages);
    }

    [Fact]
    public async Task Execute_UnknownSubcommand_ShowsUsage()
    {
        var result = await _handler.ExecuteAsync("/sessions bogus", () => Task.CompletedTask);
        Assert.Contains("用法", string.Join("", result.HistoryMessages));
    }

    [Fact]
    public async Task Execute_LoadCallsSaveFirst()
    {
        _saveCount = 0;
        var result = await _handler.ExecuteAsync("/sessions load ghost", () =>
        {
            _saveCount++;
            return Task.CompletedTask;
        });
        Assert.Equal(1, _saveCount); // saveCurrentSession was called
    }

    [Fact]
    public async Task Execute_ListAliases_Work()
    {
        var ls = await _handler.ExecuteAsync("/sessions ls", () => Task.CompletedTask);
        var list = await _handler.ExecuteAsync("/sessions list", () => Task.CompletedTask);
        Assert.Equal(ls.HistoryMessages.Count, list.HistoryMessages.Count);
    }

    [Fact]
    public async Task Execute_DeleteAllowsSubsequentList()
    {
        _handler.NewSession();
        var sessions = _sessions.ListSessions();
        if (sessions.Length > 0)
        {
            await _handler.ExecuteAsync($"/sessions delete {sessions[0].Name}", () => Task.CompletedTask);
            var list = await _handler.ExecuteAsync("/sessions list", () => Task.CompletedTask);
            Assert.NotNull(list);
        }
    }
}
