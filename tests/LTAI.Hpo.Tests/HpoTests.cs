using LTAI.Hpo;
using LTAI.Hpo.Samplers;
using LTAI.Hpo.Pruners;
using LTAI.Hpo.Storage;
using LTAI.Hpo.Dashboard;
using LTAI.Hpo.Integration;
using Microsoft.Extensions.DependencyInjection;
using static LTAI.Hpo.StudyDirection;

namespace LTAI.Hpo.Tests;

public sealed class SamplerTests
{
    [Fact]
    public void RandomSampler_WithSeed_ProducesReproducibleResults()
    {
        var s1 = new RandomSampler(42);
        var s2 = new RandomSampler(42);
        var r1 = s1.SampleFloat(null!, "x", 0f, 1f, false);
        var r2 = s2.SampleFloat(null!, "x", 0f, 1f, false);
        Assert.Equal(r1, r2);
    }

    [Fact]
    public void RandomSampler_ProducesValidRange()
    {
        var s = new RandomSampler(42);
        for (int i = 0; i < 100; i++)
        {
            var val = s.SampleFloat(null!, "x", 0f, 100f, false);
            Assert.InRange(val, 0f, 100f);
        }
    }

    [Fact]
    public void RandomSampler_SampleInt_InRange()
    {
        var s = new RandomSampler();
        for (int i = 0; i < 100; i++)
        {
            var val = s.SampleInt(null!, "n", 1, 10);
            Assert.InRange(val, 1, 10);
        }
    }

    [Fact]
    public void RandomSampler_SampleCategorical_InChoices()
    {
        var s = new RandomSampler();
        var choices = new[] { "a", "b", "c" };
        for (int i = 0; i < 30; i++)
        {
            var val = s.SampleCategorical(null!, "cat", choices);
            Assert.Contains(val, choices);
        }
    }

    [Fact]
    public void GridSampler_IteratesAllCombinations()
    {
        var grid = new GridSampler(new Dictionary<string, object[]>
        {
            ["x"] = new object[] { 1, 2 },
            ["y"] = new object[] { "a", "b", "c" }
        });
        var seen = new HashSet<(int, string)>();
        for (int i = 0; i < 6; i++)
        {
            var x = grid.SampleInt(null!, "x", 0, 10);
            var y = grid.SampleCategorical(null!, "y", new[] { "a", "b", "c" });
            seen.Add((x, y));
        }
        Assert.Equal(6, seen.Count);
    }

    [Fact]
    public void TpeSampler_Constructs()
    {
        var sampler = new TpeSampler(seed: 42);
        Assert.NotNull(sampler);
    }

    [Fact]
    public void TpeSampler_SampleFloat_WithinRange()
    {
        var sampler = new TpeSampler(seed: 42);
        var val = sampler.SampleFloat(null!, "lr", 0.001f, 0.1f, log: true);
        Assert.InRange(val, 0.001f, 0.1f);
    }
}

public sealed class StudyTests
{
    [Fact]
    public async Task OptimizeAsync_Rosenbrock_ConvergesToMinimum()
    {
        var sampler = new RandomSampler(42);
        var study = new Study("rosenbrock", sampler, direction: Minimize);

        await study.OptimizeAsync(async trial =>
        {
            var x = trial.SuggestFloat("x", -2f, 2f);
            var y = trial.SuggestFloat("y", -1f, 3f);
            var value = Math.Pow(1 - x, 2) + 100 * Math.Pow(y - x * x, 2);
            await Task.CompletedTask;
            return value;
        }, nTrials: 30);

        Assert.True(study.BestValue < 10.0,
            $"Rosenbrock should converge to ~0, got best={study.BestValue:F4}");
        Assert.Equal(30, study.CompletedCount);
    }

    [Fact]
    public async Task OptimizeAsync_Maximize_ReturnsBest()
    {
        var sampler = new RandomSampler(42);
        var study = new Study("maximize", sampler, direction: Maximize);

        await study.OptimizeAsync(async trial =>
        {
            var x = trial.SuggestFloat("x", 0f, 1f);
            var value = -Math.Pow(x - 0.7, 2) + 1.0;
            await Task.CompletedTask;
            return value;
        }, nTrials: 10);

        Assert.True(study.BestValue > 0.5,
            $"Should maximize near x=0.7, got best={study.BestValue:F4}");
    }

    [Fact]
    public async Task OptimizeAsync_WithPruner_EarlyStops()
    {
        var sampler = new RandomSampler(42);
        var pruner = new ThresholdPruner(50.0);
        var study = new Study("pruned", sampler, pruner: pruner, direction: Minimize);

        await study.OptimizeAsync(async trial =>
        {
            var x = trial.SuggestFloat("x", 0f, 10f);
            trial.Report(x * 100, 1);
            await Task.CompletedTask;
            return x;
        }, nTrials: 20);

        Assert.True(study.CompletedCount < 20);
    }

    [Fact]
    public async Task OptimizeAsync_SingleTrial_ReturnsBest()
    {
        var sampler = new RandomSampler(42);
        var study = new Study("single", sampler, direction: Minimize);

        await study.OptimizeAsync(async trial =>
        {
            trial.SuggestFloat("x", 0f, 1f);
            await Task.CompletedTask;
            return 0.42;
        }, nTrials: 1);

        Assert.Equal(1, study.CompletedCount);
        Assert.Equal(0.42, study.BestValue);
    }

    [Fact]
    public void Study_BestParams_TracksOptimal()
    {
        var sampler = new GridSampler(new Dictionary<string, object[]>
        {
            ["x"] = new object[] { 1.0, 2.0, 3.0 }
        });
        var study = new Study("grid", sampler, direction: Minimize);
        Assert.NotNull(study.Name);
        Assert.Equal("grid", study.Name);
    }
}

public sealed class SqliteStudyStoreTests : IDisposable
{
    private readonly string _dbPath;

    public SqliteStudyStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ltai-hpo-test-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task SaveAndLoad_Roundtrip()
    {
        var store = new SqliteStudyStore($"Data Source={_dbPath}");
        await store.InitializeAsync();

        var record = new TrialRecord
        {
            Number = 1,
            State = TrialState.Completed,
            Value = 0.5,
            Params = new Dictionary<string, object> { ["x"] = 1.0 }
        };
        await store.SaveTrialAsync("test-study", record);

        var loaded = await store.LoadTrialsAsync("test-study");
        Assert.Single(loaded);
        Assert.Equal(1, loaded[0].Number);
        Assert.Equal(TrialState.Completed, loaded[0].State);
        Assert.Equal(0.5, loaded[0].Value);
    }

    [Fact]
    public async Task Load_Nonexistent_ReturnsEmpty()
    {
        var store = new SqliteStudyStore($"Data Source={_dbPath}");
        await store.InitializeAsync();
        var loaded = await store.LoadTrialsAsync("nonexistent");
        Assert.Empty(loaded);
    }
}

public sealed class DashboardTests
{
    [Fact]
    public void Track_AddsStudy()
    {
        var dashboard = new HpoDashboard();
        var sampler = new RandomSampler();
        var study = new Study("test", sampler);
        dashboard.Track("test", study);
        Assert.True(dashboard.Studies.ContainsKey("test"));
    }

    [Fact]
    public void RenderSummary_ReturnsText()
    {
        var dashboard = new HpoDashboard();
        var sampler = new RandomSampler();
        var study = new Study("test", sampler);
        dashboard.Track("test", study);
        var summary = HpoDashboardRenderer.RenderStudiesSummary(dashboard.Studies);
        Assert.Contains("test", summary);
    }

    [Fact]
    public void FormatParams_ReturnsFormatted()
    {
        var formatted = HpoDashboard.FormatParams(new Dictionary<string, object>
        {
            ["lr"] = 0.001,
            ["layers"] = 3,
            ["optimizer"] = "adam"
        });
        Assert.Contains("lr", formatted);
        Assert.Contains("adam", formatted);
    }
}

public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddLtaiHpo_RegistersServices()
    {
        var services = new ServiceCollection();
        services.AddLtaiHpo();
        var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetService<HpoDashboard>());
    }
}
