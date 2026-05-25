using LTAI.Agent.Skills;
using LTAI.Agent.Skills.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LTAI.Tests;

public sealed class MultiRoundTests
{
    [Theory]
    [InlineData("hello", false)]
    [InlineData("what is the weather", false)]
    [InlineData("plan a full refactor of the authentication module. then implement JWT token validation. update all API endpoints. add integration tests. finally deploy to staging environment", true)]
    public void MultiRound_01_NeedsDecomposition(string query, bool expected)
    {
        var decomposer = new SkillAwareDecomposer(
            BuildRegistry(), null!, NullLogger<SkillAwareDecomposer>.Instance);

        Assert.Equal(expected, decomposer.NeedsDecomposition(query));
    }

    [Fact]
    public void MultiRound_02_HeuristicFallback_SplitsSentences()
    {
        var decomposer = new SkillAwareDecomposer(
            BuildRegistry(), null!, NullLogger<SkillAwareDecomposer>.Instance);

        var rounds = decomposer.DecomposeAsync(
            "review the auth module. then implement JWT support. finally add unit tests.",
            "code").GetAwaiter().GetResult();

        Assert.Equal(3, rounds.Count);
    }

    [Fact]
    public void MultiRound_03_ContextChain_BuildsPrompt()
    {
        var chain = new ContextChain(maxTokens: 8000);

        chain.AddRound(1, "analyze requirements", "Found 3 auth modules requiring update.");
        chain.AddRound(2, "implement JWT", "JWT token validation implemented in AuthService.");

        var prompt = chain.BuildPrompt("add unit tests", "skill: build_verify_loop");

        Assert.Contains("analyze requirements", prompt);
        Assert.Contains("implement JWT", prompt);
        Assert.Contains("当前步骤", prompt);
        Assert.Contains("build_verify_loop", prompt);
    }

    [Fact]
    public void MultiRound_04_ContextChain_CompactsWhenOverBudget()
    {
        var chain = new ContextChain(maxTokens: 100);

        for (int i = 0; i < 20; i++)
            chain.AddRound(i, $"step {i}", new string('x', 200));

        Assert.True(chain.History.Count <= 10);
    }

    [Fact]
    public void MultiRound_05_ContextChain_BuildsSynthesisPrompt()
    {
        var chain = new ContextChain();

        chain.AddRound(1, "review code", "Found 5 files to refactor.");
        chain.AddRound(2, "optimize queries", "Reduced query time by 40%.");

        var prompt = chain.BuildSynthesisPrompt("refactor and optimize the auth module", "code");

        Assert.Contains("refactor and optimize", prompt);
        Assert.Contains("review code", prompt);
        Assert.Contains("optimize queries", prompt);
    }

    [Fact]
    public void MultiRound_06_RoundPlan_HasMatchedSkills()
    {
        var round = new RoundPlan
        {
            Index = 1,
            Goal = "compile and verify the build",
            MatchedSkillIds = new List<string> { "build_and_verify_dsl" }
        };

        Assert.Single(round.MatchedSkillIds);
        Assert.Equal("build_and_verify_dsl", round.MatchedSkillIds[0]);
    }

    [Fact]
    public void MultiRound_07_SkillAwareDecomposer_UsesHeuristicForShortQuery()
    {
        var decomposer = new SkillAwareDecomposer(
            BuildRegistry(), null!, NullLogger<SkillAwareDecomposer>.Instance);

        var rounds = decomposer.DecomposeAsync("review auth module", "code").GetAwaiter().GetResult();

        Assert.Single(rounds);
    }

    private static LTAI.Agent.Skills.SkillRegistry BuildRegistry()
    {
        var skillsRoot = FindSkillsRoot();
        var loader = new SkillLoader(NullLogger<SkillLoader>.Instance);
        var registry = new LTAI.Agent.Skills.SkillRegistry(loader, NullLogger<LTAI.Agent.Skills.SkillRegistry>.Instance, skillsRoot);
        registry.LoadAllAsync().GetAwaiter().GetResult();
        return registry;
    }

    private static string FindSkillsRoot()
    {
        var paths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "skills"),
            Path.Combine(AppContext.BaseDirectory, "skills"),
            Path.Combine(Directory.GetCurrentDirectory(), "skills"),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", "skills")
        };

        foreach (var path in paths)
        {
            var full = Path.GetFullPath(path);
            if (Directory.Exists(full)) return full;
        }

        return Path.Combine(AppContext.BaseDirectory, "skills");
    }
}
