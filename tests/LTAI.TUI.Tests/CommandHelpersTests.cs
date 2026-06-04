using LTAI.TUI.Services;
using Xunit;

namespace LTAI.TUI.Tests;

public sealed class CommandHelpersTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(500, "500 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1.0 MB")]
    [InlineData(1572864, "1.5 MB")]
    [InlineData(1073741824, "1024.0 MB")]
    [InlineData(1610612736, "1536.0 MB")]
    public void FormatBytes_VariousSizes_ReturnsExpected(long bytes, string expected)
    {
        var result = CommandHelpers.FormatBytes(bytes);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0, "[grey]0[/]")]
    [InlineData(999, "[grey]999[/]")]
    [InlineData(1000, "[grey]1K ctx[/]")]
    [InlineData(1500, "[grey]1K ctx[/]")]
    [InlineData(10000, "[grey]10K ctx[/]")]
    [InlineData(100000, "[grey]100K ctx[/]")]
    [InlineData(1000000, "[grey]1M ctx[/]")]
    [InlineData(1500000, "[grey]1M ctx[/]")]
    public void FormatNum_VariousCounts_ReturnsExpected(int count, string expected)
    {
        var result = CommandHelpers.FormatNum(count);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void AbbrevCaps_None_ReturnsEmpty()
    {
        var result = CommandHelpers.AbbrevCaps(0);
        Assert.Equal("", result);
    }

    [Fact]
    public void SharedHttp_IsNotNull()
    {
        Assert.NotNull(CommandHelpers.SharedHttp);
    }
}
