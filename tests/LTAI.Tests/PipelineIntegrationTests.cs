using LTAI.Core.Execution;
using LTAI.Planning.HTN;
using LTAI.Planning.Models;
using LTAI.Planning.Planning;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LTAI.Tests;

public sealed class PipelineIntegrationTests
{
    [Fact]
    public void Pipeline_01_TaskPipeline_DecomposesComplexQuery()
    {
        var needsDecomp = TaskPipeline.NeedsDecomposition(
            "plan a full refactor of the authentication module with architecture review and complete migration strategy including test coverage");

        Assert.True(needsDecomp);
    }

    [Fact]
    public void Pipeline_02_TaskPipeline_NoDecomposeForSimpleQuery()
    {
        var needsDecomp = TaskPipeline.NeedsDecomposition("hello");

        Assert.False(needsDecomp);
    }

    [Fact]
    public void Pipeline_03_TaskPipeline_DecomposeNumberedList()
    {
        var pipeline = new TaskPipeline(
            new TaskJournal(NullLogger<TaskJournal>.Instance));

        var query = "\n1. review auth module\n2. implement JWT\n3. add tests\n4. update docs";
        var subtasks = pipeline.Decompose(query);

        Assert.True(subtasks.Count >= 3);
        Assert.Contains(subtasks, s => s.Contains("implement JWT"));
    }

    [Fact]
    public void Pipeline_04_TaskPipeline_DecomposeSemicolons()
    {
        var pipeline = new TaskPipeline(
            new TaskJournal(NullLogger<TaskJournal>.Instance));

        var query = "implement login authentication module; refactor the database layer for optimization; benchmark query performance";
        var subtasks = pipeline.Decompose(query);

        Assert.True(subtasks.Count >= 3);
    }

    [Fact]
    public void Pipeline_05_TaskJournal_TracksEntries()
    {
        var journal = new TaskJournal(NullLogger<TaskJournal>.Instance);

        var entry = journal.Add("test task");
        Assert.NotNull(entry);
        Assert.NotNull(entry.Id);

        journal.Complete(entry, "result");
        Assert.True(entry.CompletedAt.HasValue);
    }

    [Fact]
    public void Pipeline_06_HTNPlanner_DecomposeSimpleTask()
    {
        var planner = new HTNPlanner(NullLogger<HTNPlanner>.Instance);

        var tools = new List<string> { "filesystem", "shell", "code", "git" };
        var plan = planner.DecomposeTask("analyze performance bottlenecks", "code", tools);

        Assert.NotNull(plan);
        Assert.NotEmpty(plan.Name);
    }

    [Fact]
    public void Pipeline_07_HTNPlanner_StoreAndReuse()
    {
        var planner = new HTNPlanner(NullLogger<HTNPlanner>.Instance);

        var tools = new List<string> { "filesystem", "shell", "code" };
        var plan = planner.DecomposeTask("refactor database connection pooling", "code", tools);
        plan.IsReusable = true;

        planner.StorePlan(plan, success: true);

        var templates = planner.GetTemplatesByDomain("code");
        Assert.NotEmpty(templates);
    }

    [Fact]
    public void Pipeline_08_CheckpointManager_SaveAndLoad()
    {
        var checkpoint = new TaskCheckpoint(
            Path.Combine(Path.GetTempPath(), "ltai_test_checkpoints"),
            NullLogger<TaskCheckpoint>.Instance);

        var sessionId = $"test_session_{Guid.NewGuid():N}";
        try
        {
            var state = new CheckpointState
            {
                SessionId = sessionId,
                Plan = new List<string> { "1. analyze", "2. implement", "3. test" },
                CompletedSteps = new List<string> { "1. analyze" }
            };

            checkpoint.SaveAsync(sessionId, state).GetAwaiter().GetResult();

            var loaded = checkpoint.LoadAsync(sessionId).GetAwaiter().GetResult();
            Assert.NotNull(loaded);
            Assert.Equal(sessionId, loaded!.SessionId);
            Assert.Single(loaded.CompletedSteps);

            var resumed = checkpoint.ResumeAsync(sessionId).GetAwaiter().GetResult();
            Assert.NotNull(resumed);
            Assert.True(resumed!.Plan.Count <= 3);
        }
        finally
        {
            checkpoint.Delete(sessionId);
        }
    }

    [Fact]
    public void Pipeline_09_CheckpointManager_VersionIncrements()
    {
        var checkpoint = new TaskCheckpoint(
            Path.Combine(Path.GetTempPath(), "ltai_test_checkpoints_v2"),
            NullLogger<TaskCheckpoint>.Instance);

        var sessionId = $"version_test_{Guid.NewGuid():N}";
        try
        {
            var state = new CheckpointState { SessionId = sessionId, Plan = new List<string> { "step" } };
            checkpoint.SaveAsync(sessionId, state).GetAwaiter().GetResult();
            var v1 = checkpoint.LoadAsync(sessionId).GetAwaiter().GetResult();

            state = new CheckpointState { SessionId = sessionId, Plan = new List<string> { "step", "step2" } };
            checkpoint.SaveAsync(sessionId, state).GetAwaiter().GetResult();
            var v2 = checkpoint.LoadAsync(sessionId).GetAwaiter().GetResult();

            Assert.NotNull(v1);
            Assert.NotNull(v2);
            Assert.True(v2!.Version >= v1!.Version);
        }
        finally
        {
            checkpoint.Delete(sessionId);
        }
    }

    [Fact]
    public void Pipeline_10_GtsmPlanner_ProducesValidTrajectory()
    {
        var planner = GtsmPlanner.Instance;

        var trajectory = planner.Plan("design a caching layer", GTSMMode.Hybrid, "code");

        Assert.NotNull(trajectory);
        Assert.NotEmpty(trajectory.Steps);
        Assert.All(trajectory.Steps, s => Assert.False(string.IsNullOrWhiteSpace(s.Action)));
    }

    [Fact]
    public void Pipeline_11_DiffusionPlanner_RefineReturnsPlan()
    {
        var planner = DiffusionPlanner.Instance.Value;

        var plan = planner.Refine("analyze", "code").GetAwaiter().GetResult();

        Assert.NotNull(plan);
        Assert.NotNull(plan.FinalPlan);
        Assert.NotEmpty(plan.Steps);
    }
}
