using LTAI.Agent.Format;
using Xunit;

namespace LTAI.Tests;

public sealed class BabelTeleFormatterTests
{
    public BabelTeleFormatterTests()
    {
        BabelTeleFormatter.ResetForContext();
    }

    [Fact]
    public void EncodeToolResult_ProducesCompactToken()
    {
        var result = BabelTeleFormatter.EncodeToolResult("ReadFileContent", "path=test.cs", "hello world", 1);
        Assert.Contains("[T:ReadFileContent#1]", result);
        Assert.Contains("hello", result);
    }

    [Fact]
    public void EncodeSearchResult_ContainsPatternAndCount()
    {
        var result = BabelTeleFormatter.EncodeSearchResult("main", 5, "src/test.cs", 10);
        Assert.Contains("m=5", result);
        Assert.Contains("test.cs:L10", result);
    }

    [Fact]
    public void EncodeRef_ContainsPathAndLine()
    {
        var result = BabelTeleFormatter.EncodeRef("src/test.cs", 42);
        Assert.Contains("test.cs#L42", result);
    }

    [Fact]
    public void EncodeError_ContainsCodeAndLine()
    {
        var result = BabelTeleFormatter.EncodeError("CS1002", 5, 10, "semicolon expected");
        Assert.Contains("E:CS1002", result);
        Assert.Contains("L5:10", result);
    }

    [Fact]
    public void EncodeGraphResult_ContainsQueryAndCount()
    {
        var result = BabelTeleFormatter.EncodeGraphResult("searchSymbol", 5, null);
        Assert.Contains("n=5", result);
    }

    [Fact]
    public void SelfExplaining_FirstUseHasExpansion()
    {
        BabelTeleFormatter.ResetForContext();
        var first = BabelTeleFormatter.EncodeToolResult("ReadFile", "path=x", "data", 1);
        Assert.Contains("## [T:tool]", first);
    }

    [Fact]
    public void SelfExplaining_SecondUseNoExpansion()
    {
        BabelTeleFormatter.ResetForContext();
        _ = BabelTeleFormatter.EncodeToolResult("ReadFile", "path=x", "data", 1);
        var second = BabelTeleFormatter.EncodeToolResult("WriteFile", "path=y", "data", 2);
        Assert.DoesNotContain("## [T:tool]", second);
    }
}
