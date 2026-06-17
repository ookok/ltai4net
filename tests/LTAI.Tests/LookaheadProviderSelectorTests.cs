using LTAI.Agent.Context;
using LTAI.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace LTAI.Tests;

public class LookaheadProviderSelectorTests
{
    [Fact]
    public void IsProviderSkipped_ReturnsTrue_WhenProviderInRouteTag()
    {
        var ctx = new AIContext
        {
            Messages = [
                new ChatMessage(ChatRole.System, "<provider-route skip=\"KbGraph,CgGraph\" />"),
            ]
        };

        Assert.True(ctx.IsProviderSkipped("KbGraph"));
        Assert.True(ctx.IsProviderSkipped("CgGraph"));
        Assert.False(ctx.IsProviderSkipped("L4DeepSearch"));
    }

    [Fact]
    public void IsProviderSkipped_ReturnsFalse_WhenNoRoute()
    {
        var ctx = new AIContext
        {
            Messages = [new ChatMessage(ChatRole.System, "no route here")]
        };

        Assert.False(ctx.IsProviderSkipped("KbGraph"));
    }

    [Fact]
    public void IsProviderSkipped_HandlesEmptyMessages()
    {
        var ctx = new AIContext { Messages = [] };
        Assert.False(ctx.IsProviderSkipped("Anything"));
    }

    [Fact]
    public void IsProviderSkipped_MultipleProvidersInOneTag()
    {
        var ctx = new AIContext
        {
            Messages = [new ChatMessage(ChatRole.System,
                "<provider-route skip=\"KbGraph,CgGraph,CodeChunkIndex,WasmtimeSandbox\" />")]
        };

        Assert.True(ctx.IsProviderSkipped("KbGraph"));
        Assert.True(ctx.IsProviderSkipped("CodeChunkIndex"));
        Assert.False(ctx.IsProviderSkipped("L4DeepSearch"));
    }

    [Fact]
    public void IsProviderSkipped_CaseInsensitive()
    {
        var ctx = new AIContext
        {
            Messages = [new ChatMessage(ChatRole.System, "<provider-route skip=\"kbgraph\" />")]
        };

        Assert.True(ctx.IsProviderSkipped("KbGraph"));
        Assert.True(ctx.IsProviderSkipped("KBGRAPH"));
    }
}
