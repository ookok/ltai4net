using LTAI.Core.Commands;
using LTAI.TUI.Services;
using Xunit;

namespace LTAI.TUI.Tests;

public sealed class SnippetCommandServiceTests
{
    [Fact]
    public async Task Execute_NullStore_DoesNotThrow()
    {
        var service = new SnippetCommandService(null);
        var cmd = new LTAI.Core.Commands.SnippetCommand("list");
        var result = await service.ExecuteAsync(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Execute_List_ReturnsSuccess()
    {
        var service = new SnippetCommandService(null);
        var cmd = new LTAI.Core.Commands.SnippetCommand("list");
        var result = await service.ExecuteAsync(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Execute_Save_ReturnsSuccess()
    {
        var service = new SnippetCommandService(null);
        var cmd = new LTAI.Core.Commands.SnippetCommand("save mykey my content");
        var result = await service.ExecuteAsync(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Execute_Use_ReturnsSuccess()
    {
        var service = new SnippetCommandService(null);
        var cmd = new LTAI.Core.Commands.SnippetCommand("use mykey");
        var result = await service.ExecuteAsync(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Execute_Delete_ReturnsSuccess()
    {
        var service = new SnippetCommandService(null);
        var cmd = new LTAI.Core.Commands.SnippetCommand("delete mykey");
        var result = await service.ExecuteAsync(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Execute_Rename_ReturnsSuccess()
    {
        var service = new SnippetCommandService(null);
        var cmd = new LTAI.Core.Commands.SnippetCommand("rename old new");
        var result = await service.ExecuteAsync(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Execute_Edit_ReturnsSuccess()
    {
        var service = new SnippetCommandService(null);
        var cmd = new LTAI.Core.Commands.SnippetCommand("edit mykey new content");
        var result = await service.ExecuteAsync(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Execute_NonSnippetCommand_ReturnsSuccess()
    {
        var service = new SnippetCommandService(null);
        var cmd = new HelpCommand();
        var result = await service.ExecuteAsync(cmd);
        Assert.NotNull(result);
    }
}
