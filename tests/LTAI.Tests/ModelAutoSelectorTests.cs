using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LTAI.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LTAI.Tests;

public sealed class ModelScoringEngineTests
{
    private static ModelInfo M(string id, string family, bool toolCall = false,
        bool reasoning = false, bool structuredOutput = false, bool temperature = false,
        string[]? modalities = null, int ctx = 8192,
        decimal priceIn = 0, decimal priceOut = 0)
        => new(id, id, family, toolCall, reasoning, structuredOutput, false, temperature,
            modalities ?? ["text"], ["text"], ctx, 4096,
            priceIn, priceOut, null, null, null, false);

    private readonly ModelScoringEngine _e = new(NullLogger<ModelScoringEngine>.Instance);

    [Fact]
    public void Score_Qualified_ReturnsPositive()
        => Assert.True(_e.Score(M("x", "flash-1", toolCall: true, temperature: true, ctx: 64000),
            ModelTierRequirements.L2) > 0);

    [Fact]
    public void Score_NoToolCall_Zero() => Assert.Equal(0,
        _e.Score(M("x", "flash-1", toolCall: false, temperature: true, ctx: 64000),
            ModelTierRequirements.L2));

    [Fact]
    public void Score_NoStreaming_Zero() => Assert.Equal(0,
        _e.Score(M("x", "flash-1", toolCall: true, temperature: false, ctx: 32000),
            ModelTierRequirements.L1));

    [Fact]
    public void Score_SmallContext_Zero() => Assert.Equal(0,
        _e.Score(M("x", "flash-1", toolCall: true, temperature: true, ctx: 4000),
            ModelTierRequirements.L1));

    [Fact]
    public void Score_SlowLatency_Zero() => Assert.Equal(0,
        _e.Score(M("x", "pro-1", toolCall: true, temperature: true, ctx: 64000),
            ModelTierRequirements.L3));

    [Fact]
    public void Score_MoreCapable_Higher()
    {
        var b = _e.Score(M("base", "flash-1", toolCall: true, temperature: true, ctx: 64000),
            ModelTierRequirements.L2);
        var f = _e.Score(M("full", "flash-1", toolCall: true, temperature: true,
                structuredOutput: true, ctx: 200000), ModelTierRequirements.L2);
        Assert.True(f > b);
    }

    [Fact]
    public void Score_Cheaper_HigherForL3()
    {
        var c = _e.Score(M("c", "flash-1", priceIn: 0.15m, priceOut: 0.60m, ctx: 64000),
            ModelTierRequirements.L3);
        var e = _e.Score(M("e", "flash-1", priceIn: 15m, priceOut: 60m, ctx: 64000),
            ModelTierRequirements.L3);
        Assert.True(c > e);
    }

    [Fact]
    public void Score_FastBeatsSlow()
    {
        var f = _e.Score(M("f", "flash-1", ctx: 32000), ModelTierRequirements.L3);
        var s = _e.Score(M("s", "pro-1", ctx: 32000), ModelTierRequirements.L3);
        Assert.True(f >= s);
    }

    [Fact]
    public void SelectBestPair_NoneQualified()
    {
        var (p, a) = _e.SelectBestPair(
            [M("x", "flash-1", toolCall: false, ctx: 4000)],
            ModelTierRequirements.L2);
        Assert.Null(p); Assert.Null(a);
    }

    [Fact]
    public void SelectBestPair_Single_ReturnsPrimaryOnly()
    {
        var (p, a) = _e.SelectBestPair(
            [M("only", "flash-1", toolCall: true, temperature: true, ctx: 64000)],
            ModelTierRequirements.L2);
        Assert.NotNull(p); Assert.Null(a);
        Assert.Equal("only", p!.ShortId);
    }

    [Fact]
    public void SelectBestPair_Multiple_ReturnsTopTwo()
    {
        var (p, a) = _e.SelectBestPair(new[]
        {
            M("best", "flash-1", toolCall: true, temperature: true,
                structuredOutput: true, reasoning: true, ctx: 128000),
            M("mid", "flash-1", toolCall: true, temperature: true, ctx: 64000),
            M("worst", "flash-1", toolCall: true, temperature: true, ctx: 32000),
        }, ModelTierRequirements.L2);
        Assert.NotNull(p); Assert.NotNull(a);
        Assert.Equal("best", p!.ShortId);
    }


}

public sealed class ResolveProviderIdTests
{
    [Theory]
    [InlineData("deepseek", "deepseek")]
    [InlineData("deepseek-fast", "deepseek")]
    [InlineData("deepseek-pro", "deepseek")]
    [InlineData("DeepSeek", "deepseek")]
    [InlineData("SiliconFlow", "siliconflow")]
    [InlineData("Aliyun(Qwen)", "alibaba")]
    [InlineData("Zhipu(GLM)", "zhipuai")]
    [InlineData("OpenAI", "openai")]
    [InlineData("Anthropic", "anthropic")]
    [InlineData("OpenRouter", "openrouter")]
    [InlineData("StepFun", "stepfun")]
    [InlineData("unknown", null)]
    public void MapsCorrectly(string input, string? expected)
    {
        var method = typeof(ModelAutoSelectHostedService).GetMethod("ResolveProviderId",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = method?.Invoke(null, [input]) as string;
        Assert.Equal(expected, result);
    }
}
