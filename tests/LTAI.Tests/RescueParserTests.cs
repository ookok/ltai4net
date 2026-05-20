using LTAI.AI.Providers;
using System.Text.Json;
using Xunit;

namespace LTAI.Tests;

public class RescueParserTests
{
    [Fact]
    public void StandardParse_ValidJson()
    {
        var result = RescueParser.TryParseToolCall("""{"tool":"math_eval","expression":"2+3"}""");
        Assert.NotNull(result);
    }

    [Fact]
    public void FixQuotes_MissingQuotesOnKeys()
    {
        var result = RescueParser.TryParseToolCall("{tool: math_eval, expression: 2+3}");
        Assert.NotNull(result);
    }

    [Fact]
    public void FixQuotes_SingleQuotes()
    {
        var result = RescueParser.TryParseToolCall("""{'tool':'math_eval','expression':'2+3'}""");
        Assert.NotNull(result);
    }

    [Fact]
    public void FixBraces_MissingClosingBrace()
    {
        var result = RescueParser.TryParseToolCall("""{"tool":"math_eval","expression":"2+3" """);
        Assert.NotNull(result);
    }

    [Fact]
    public void ExtractJsonBlock_FromMixedText()
    {
        var result = RescueParser.TryParseToolCall("""Here is the result: {"tool":"math_eval","expression":"2+3"} Done.""");
        Assert.NotNull(result);
    }

    [Fact]
    public void FixTrailingComma()
    {
        var result = RescueParser.TryParseToolCall("""{"tool":"math_eval","expression":"2+3",}""");
        Assert.NotNull(result);
    }

    [Fact]
    public void NullInput_ReturnsNull()
    {
        Assert.Null(RescueParser.TryParseToolCall(""));
        Assert.Null(RescueParser.TryParseToolCall(null!));
    }
}
