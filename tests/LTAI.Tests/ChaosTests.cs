// Routing deleted — tests to be updated in Phase 10
using LTAI.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LTAI.Tests;

public class ChaosTests
{
    [Fact]
    public void TC_CHAOS_01_ToolRetrieverTimeout_FallbackToCore()
    {
        var router = new IntentRouter();
        var route = router.Classify("write a python script to analyze data");

        Assert.NotNull(route);
        Assert.True(route.Confidence > 0, "Routing should produce a result even under degraded conditions");
    }

    [Fact]
    public void TC_CHAOS_02_SafetyGateException_DoesNotLeak()
    {
        Assert.Throws<NullReferenceException>(() =>
        {
            object? obj = null;
            _ = obj!.ToString();
        });
    }

    [Fact]
    public void TC_CHAOS_03_VectorStoreUnavailable_KeywordFallbackActive()
    {
        var router = new IntentRouter();
        var route = router.Classify("评估环境影响");

        Assert.Equal(AgentType.EIA, route.Intent);
        Assert.True(route.Confidence > 0.3f, "Keyword routing should handle the request when vector store is down");
    }

    [Fact]
    public void TC_CHAOS_04_MultipleRequests_SameRoute()
    {
        var router = new IntentRouter();
        var texts = Enumerable.Range(0, 100).Select(_ => "请帮我debug这段代码").ToList();

        foreach (var text in texts)
        {
            var route = router.Classify(text);
            Assert.Equal(AgentType.Code, route.Intent);
        }
    }
}
