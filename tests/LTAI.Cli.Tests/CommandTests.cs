using LTAI.Core.Configuration;

namespace LTAI.Cli.Tests;

public sealed class ProgramEntryTests
{
    [Fact]
    public void Version_ReturnsZero()
    {
        var exitCode = LTAI.Cli.Program.Main(new[] { "--version" }).GetAwaiter().GetResult();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Help_ReturnsZero()
    {
        var exitCode = LTAI.Cli.Program.Main(new[] { "--help" }).GetAwaiter().GetResult();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void NoArgs_ReturnsOne()
    {
        var exitCode = LTAI.Cli.Program.Main(Array.Empty<string>()).GetAwaiter().GetResult();
        Assert.Equal(1, exitCode);
    }
}

public sealed class SecretManagerCliTests
{
    [Fact]
    public void SetAndGet_Roundtrip()
    {
        var key = "LTAI_UT_CLI_" + Guid.NewGuid().ToString("N")[..8];
        SecretManager.Set(key, "cli-test-value", persistent: false);
        Assert.Equal("cli-test-value", SecretManager.Get(key));
        SecretManager.Invalidate(key);
    }
}
