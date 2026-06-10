using LTAI.Core.Commands;
using LTAI.Core.Configuration;
using LTAI.TUI.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace LTAI.TUI.Tests;

public sealed class ConfigCommandServiceTests
{
    private static readonly IOptions<LTAIOptions> Options = Microsoft.Extensions.Options.Options.Create(new LTAIOptions());

    [Fact]
    public async Task Execute_NullRouterAndOptions_DoesNotThrow()
    {
        var service = new ConfigCommandService(null, Options);
        var cmd = new ConfigCommand("status");
        var result = await service.ExecuteAsync(cmd);
        Assert.NotNull(result);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("l1")]
    [InlineData("l2")]
    public async Task Execute_ConfigSubCommands_DoesNotThrow(string args)
    {
        var service = new ConfigCommandService(null, Options);
        var cmd = new ConfigCommand(args);
        var result = await service.ExecuteAsync(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Execute_ConfigUnknownSubcommand_DoesNotThrow()
    {
        var service = new ConfigCommandService(null, Options);
        var cmd = new ConfigCommand("unknown_sub");
        var result = await service.ExecuteAsync(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Execute_ConfigApikey_ThrowsInNonInteractive() // interactive prompt not available in test runner
    {
        var service = new ConfigCommandService(null, Options);
        var cmd = new ConfigCommand("apikey");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(cmd));
    }

    [Fact]
    public async Task Execute_ConfigExport_DoesNotThrow()
    {
        var service = new ConfigCommandService(null, Options);
        var cmd = new ConfigCommand("export");
        var result = await service.ExecuteAsync(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Execute_ConfigImport_DoesNotThrow()
    {
        var service = new ConfigCommandService(null, Options);
        var cmd = new ConfigCommand("import");
        var result = await service.ExecuteAsync(cmd);
        Assert.NotNull(result);
    }
}
