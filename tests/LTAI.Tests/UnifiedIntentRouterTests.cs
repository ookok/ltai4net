using LTAI.Agent.Routing;
using LTAI.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LTAI.Tests;

public class UnifiedIntentRouterTests
{
    private readonly UnifiedIntentRouter _router;

    public UnifiedIntentRouterTests()
    {
        var intentRouter = new IntentRouter();
        _router = new UnifiedIntentRouter(NullLogger<UnifiedIntentRouter>.Instance, intentRouter);
    }

    [Fact]
    public void Route_CodeRequest_ReturnsCodeIntent()
    {
        var route = _router.Route("Fix this bug in the code");
        Assert.Equal(AgentType.Code, route.Intent);
        Assert.Equal(AgentType.Code, route.TargetAgent);
    }

    [Fact]
    public void Route_EIARequest_ReturnsEIAIntent()
    {
        var route = _router.Route("分析这个化工厂的环境影响");
        Assert.Equal(AgentType.EIA, route.Intent);
    }

    [Fact]
    public void Route_DetectsQueryShape()
    {
        var route = _router.Route("What is the definition of machine learning?");
        Assert.NotNull(route.QueryShape);
        Assert.Equal("ExactLookup", route.QueryShape);
    }

    [Fact]
    public void Route_DetectsWorkflowTrigger()
    {
        var route = _router.Route("Please analyze and review the architecture design plan for the new system");
        Assert.True(route.UseWorkflow);
    }

    [Fact]
    public void Route_ShortQuery_NoWorkflow()
    {
        var route = _router.Route("Hello");
        Assert.False(route.UseWorkflow);
    }

    [Fact]
    public void Route_EmptyInput_ReturnsChat()
    {
        var route = _router.Route("");
        Assert.Equal(AgentType.Chat, route.Intent);
        Assert.Equal(1.0f, route.Confidence);
    }

    [Fact]
    public void RouteAll_ReturnsMultipleIntents()
    {
        var routes = _router.RouteAll("分析这段代码的环境影响评估");
        Assert.True(routes.Count >= 1);
    }

    [Fact]
    public void Route_SourceIsUnified()
    {
        var route = _router.Route("test");
        Assert.Equal("unified", route.Source);
    }

    [Fact]
    public void Route_LongQuery_TriggersWorkflow()
    {
        var longQuery = string.Join(" ", Enumerable.Repeat("word", 60));
        var route = _router.Route(longQuery);
        Assert.True(route.UseWorkflow);
    }

    [Fact]
    public void Route_MultiSentenceQuery_TriggersWorkflow()
    {
        var route = _router.Route("First, analyze the code. Then, review the design. Finally, plan the implementation.");
        Assert.True(route.UseWorkflow);
    }
}
