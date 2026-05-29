// Routing deleted — tests to be updated in Phase 10
using LTAI.Models;
using Xunit;

namespace LTAI.Tests;

public class IntentRouterTests
{
    private readonly IntentRouter _router = new();

    [Fact]
    public void EmptyInput_ReturnsChat()
    {
        var route = _router.Classify("");
        Assert.Equal(AgentType.Chat, route.Intent);
        Assert.Equal(AgentType.Chat, route.TargetAgent);
        Assert.Equal(1.0f, route.Confidence);
    }

    [Fact]
    public void NullInput_ReturnsChat()
    {
        var route = _router.Classify(null!);
        Assert.Equal(AgentType.Chat, route.Intent);
    }

    [Fact]
    public void CodeKeywords_RouteToCode()
    {
        var route = _router.Classify("Please debug this function and refactor the class");
        Assert.Equal(AgentType.Code, route.Intent);
        Assert.Equal(AgentType.Code, route.TargetAgent);
        Assert.True(route.Confidence >= 0.85f);
        Assert.Contains("debug", route.MatchedKeywords);
        Assert.Contains("function ", route.MatchedKeywords);
    }

    [Fact]
    public void EiaChineseKeywords_RouteToEia()
    {
        var route = _router.Classify("请评估这个项目的环境影响，包括大气排放和噪声污染");
        Assert.Equal(AgentType.EIA, route.Intent);
        Assert.Equal(AgentType.EIA, route.TargetAgent);
        Assert.True(route.Confidence > 0.8f);
    }

    [Fact]
    public void EiaEnglishKeywords_RouteToEia()
    {
        var route = _router.Classify("Analyze the air quality impact and dispersion modeling for this factory");
        Assert.Equal(AgentType.EIA, route.Intent);
        Assert.True(route.MatchedKeywords.Count >= 2);
    }

    [Fact]
    public void ReasoningKeywords_RouteToReasoning()
    {
        var route = _router.Classify("为什么这个算法的复杂度是O(n log n)？请分析并比较不同方案");
        Assert.Equal(AgentType.Reasoning, route.Intent);
        Assert.Contains("为什么", route.MatchedKeywords);
    }

    [Fact]
    public void EiaCriticReview_RoutesToCritic()
    {
        var route = _router.Classify("请审核这份环评报告的合规性，检查标准引用");
        Assert.Equal(AgentType.EiaCritic, route.Intent);
        Assert.Equal(AgentType.EiaCritic, route.TargetAgent);
    }

    [Fact]
    public void UnknownInput_ReturnsChatWithLowConfidence()
    {
        var route = _router.Classify("xyzzy flibble wobble");
        Assert.Equal(AgentType.Chat, route.Intent);
        Assert.True(route.Confidence <= 0.7f);
        Assert.Empty(route.MatchedKeywords);
    }

    [Fact]
    public void MultiIntent_ReturnsBestMatch()
    {
        var route = _router.Classify("Help me debug the environmental impact code");
        Assert.Equal(AgentType.Code, route.Intent);
        Assert.True(route.Confidence > 0.8f);
    }

    [Fact]
    public void ClassifyAll_MultipleIntents_ReturnsOrderedByConfidence()
    {
        var routes = _router.ClassifyAll("analyze the code and environmental impact");
        Assert.True(routes.Count >= 2);
        Assert.True(routes[0].Confidence >= routes[^1].Confidence);
    }

    [Fact]
    public void ClassifyAll_SingleIntent_ReturnsOneResult()
    {
        var routes = _router.ClassifyAll("hello how are you");
        Assert.Single(routes);
        Assert.Equal(AgentType.Chat, routes[0].Intent);
    }

    [Fact]
    public void ArchitectureKeywords_RouteToReasoning()
    {
        var route = _router.Classify("Design the architecture for a microservice system");
        Assert.Equal(AgentType.Reasoning, route.Intent);
    }

    [Fact]
    public void ChineseEmissions_RoutesToEia()
    {
        var route = _router.Classify("计算温室气体排放量，评估碳排放影响");
        Assert.Equal(AgentType.EIA, route.Intent);
        Assert.Contains("温室", route.MatchedKeywords);
    }
}
