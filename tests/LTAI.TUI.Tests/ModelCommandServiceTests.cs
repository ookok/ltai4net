using LTAI.Core.Commands;
using LTAI.Core.Configuration;
using LTAI.TUI.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace LTAI.TUI.Tests;

public sealed class ModelCommandServiceTests
{
    private static readonly IOptions<LTAIOptions> Options = Microsoft.Extensions.Options.Options.Create(new LTAIOptions());

    [Fact]
    public void Execute_NullRouter_DoesNotThrow()
    {
        var service = new ModelCommandService(null, null, null, Options);
        var cmd = new ModelCommand("");
        var result = service.Execute(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public void Execute_ModelsCommand_DoesNotThrow()
    {
        var service = new ModelCommandService(null, null, null, Options);
        var cmd = new ModelsCommand();
        var result = service.Execute(cmd);
        Assert.NotNull(result);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("l0")]
    public void Execute_ModelSubCommands_DoesNotThrow(string args)
    {
        var service = new ModelCommandService(null, null, null, Options);
        var cmd = new ModelCommand(args);
        var result = service.Execute(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public void Execute_ModelWithUnknownArgs_DoesNotThrow()
    {
        var service = new ModelCommandService(null, null, null, Options);
        var cmd = new ModelCommand("unknown_subcommand");
        var result = service.Execute(cmd);
        Assert.NotNull(result);
    }
}
