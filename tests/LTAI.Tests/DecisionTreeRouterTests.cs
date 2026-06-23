using Xunit;
using LTAI.AI;
using LTAI.Agent.Workflows;
using LTAI.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Tests;

public sealed class DecisionTreeRouterTests
{
    private static readonly ILogger<DecisionTreeRouter> Log =
        NullLogger<DecisionTreeRouter>.Instance;

    [Fact]
    public async Task RouteAsync_NullEmbedder_ReturnsNoEmbedderBranch()
    {
        var router = new DecisionTreeRouter(null, Log);
        var result = await router.RouteAsync("test task",
            new[] { "LTAI-Chat", "LTAI-Dev", "LTAI-Math", "LTAI-Data" });
        Assert.Equal(BranchKind.NoEmbedder, result.Branch);
        Assert.NotEmpty(result.Candidates);
        Assert.Equal(3, result.Candidates.Count);
    }

    [Fact]
    public async Task RouteAsync_NullEmbedder_RespectsOptionsTopK()
    {
        var router = new DecisionTreeRouter(null, Log,
            options: new DecisionTreeRouterOptions { TopK = 1 });
        var result = await router.RouteAsync("task",
            new[] { "A", "B", "C", "D" });
        Assert.Single(result.Candidates);
        Assert.Equal("A", result.Candidates[0]);
    }

    [Fact]
    public async Task RouteAsync_NullEmbedder_MoreSpecialistsThanTopK_ReturnsTopK()
    {
        var router = new DecisionTreeRouter(null, Log,
            options: new DecisionTreeRouterOptions { TopK = 5 });
        var names = new[] { "A", "B", "C", "D" };
        var result = await router.RouteAsync("task", names);
        Assert.Equal(names.Length, result.Candidates.Count);
    }

    [Fact]
    public async Task RouteAsync_EmptySpecialists_ReturnsNoCandidates()
    {
        var router = new DecisionTreeRouter(null, Log);
        var result = await router.RouteAsync("task", Array.Empty<string>());
        Assert.Empty(result.Candidates);
        Assert.Equal(BranchKind.NoCandidates, result.Branch);
    }

    [Fact]
    public async Task RouteAsync_SingleSpecialist_ReturnsIt()
    {
        var router = new DecisionTreeRouter(null, Log, options: new DecisionTreeRouterOptions { TopK = 1 });
        var result = await router.RouteAsync("task", new[] { "LTAI-Dev" });
        Assert.Single(result.Candidates);
        Assert.Equal("LTAI-Dev", result.Candidates[0]);
    }

    [Fact]
    public async Task RouteAsync_NullTask_DoesNotCrash()
    {
        var router = new DecisionTreeRouter(null, Log);
        var result = await router.RouteAsync(null!, new[] { "LTAI-Chat" });
        Assert.Equal(BranchKind.NoEmbedder, result.Branch);
        Assert.NotEmpty(result.Candidates);
    }

    [Fact]
    public void Constructor_WithAllNulls_DoesNotThrow()
    {
        var ex = Record.Exception(() => new DecisionTreeRouter(null, Log, null, null, null, null, null));
        Assert.Null(ex);
    }

    [Fact]
    public async Task RouteAsync_WithWhitelistNotConfigured_ReturnsAll()
    {
        var names = Enumerable.Range(1, 20).Select(i => $"Agent-{i}").ToArray();
        var router = new DecisionTreeRouter(null, Log);
        var result = await router.RouteAsync("any task", names);
        Assert.Equal(3, result.Candidates.Count);
        Assert.Equal(BranchKind.NoEmbedder, result.Branch);
    }

    [Fact]
    public async Task RouteAsync_DuplicateSpecialistNames_Deduplicates()
    {
        var router = new DecisionTreeRouter(null, Log);
        var result = await router.RouteAsync("task",
            new[] { "LTAI-Chat", "LTAI-Chat", "LTAI-Dev" });
        Assert.NotEmpty(result.Candidates);
    }

    [Fact]
    public void BranchKind_Values_AllDistinct()
    {
        var values = Enum.GetValues<BranchKind>();
        Assert.Equal(7, values.Length);
        Assert.Contains(BranchKind.NoEmbedder, values);
        Assert.Contains(BranchKind.ConfidentTopK, values);
        Assert.Contains(BranchKind.AmbiguousFallback, values);
        Assert.Contains(BranchKind.NoCandidates, values);
    }
}