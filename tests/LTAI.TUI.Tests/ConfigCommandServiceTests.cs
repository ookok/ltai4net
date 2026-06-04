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
    public void Execute_NullRouterAndOptions_DoesNotThrow()
    {
        var service = new ConfigCommandService(null, Options);
        var cmd = new ConfigCommand("status");
        var result = service.Execute(cmd);
        Assert.NotNull(result);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("l1")]
    [InlineData("l2")]
    public void Execute_ConfigSubCommands_DoesNotThrow(string args)
    {
        var service = new ConfigCommandService(null, Options);
        var cmd = new ConfigCommand(args);
        var result = service.Execute(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public void Execute_ConfigUnknownSubcommand_DoesNotThrow()
    {
        var service = new ConfigCommandService(null, Options);
        var cmd = new ConfigCommand("unknown_sub");
        var result = service.Execute(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public void Execute_ConfigApikey_DoesNotThrow()
    {
        var service = new ConfigCommandService(null, Options);
        var cmd = new ConfigCommand("apikey");
        var result = service.Execute(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public void Execute_ConfigExport_DoesNotThrow()
    {
        var service = new ConfigCommandService(null, Options);
        var cmd = new ConfigCommand("export");
        var result = service.Execute(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public void Execute_ConfigImport_DoesNotThrow()
    {
        var service = new ConfigCommandService(null, Options);
        var cmd = new ConfigCommand("import");
        var result = service.Execute(cmd);
        Assert.NotNull(result);
    }
}
