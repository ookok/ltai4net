using LTAI.Agent.Feedback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LTAI.Tests;

public class ABExperimentEngineTests
{
    private readonly ABExperimentEngine _engine = new(NullLogger<ABExperimentEngine>.Instance);

    [Fact]
    public void CreateExperiment_ReturnsExperimentWithVariants()
    {
        var exp = _engine.CreateExperiment("test1", "Test experiment", new[] { "control", "variant_a", "variant_b" });

        Assert.NotNull(exp.Id);
        Assert.Equal("test1", exp.Name);
        Assert.Equal(3, exp.Variants.Count);
        Assert.Contains("control", exp.Variants.Keys);
        Assert.Contains("variant_a", exp.Variants.Keys);
        Assert.Contains("variant_b", exp.Variants.Keys);
        Assert.Equal(ExperimentStatus.Draft, exp.Status);
    }

    [Fact]
    public void StartExperiment_ChangesStatusToRunning()
    {
        var exp = _engine.CreateExperiment("test2", "desc", new[] { "a", "b" });
        _engine.StartExperiment(exp.Id);

        var status = _engine.GetStatus();
        Assert.Equal(1, status["running"]);
    }

    [Fact]
    public void AssignVariant_NotRunning_ReturnsControl()
    {
        var exp = _engine.CreateExperiment("test3", "desc", new[] { "a", "b" });
        // Not started yet
        var variant = _engine.AssignVariant(exp.Id, "session1");
        Assert.Equal("control", variant);
    }

    [Fact]
    public void AssignVariant_Running_ReturnsValidVariant()
    {
        var exp = _engine.CreateExperiment("test4", "desc", new[] { "control", "treatment" });
        _engine.StartExperiment(exp.Id);

        var variant = _engine.AssignVariant(exp.Id, "session2");
        Assert.True(variant == "control" || variant == "treatment");
    }

    [Fact]
    public void AssignVariant_SameSession_ReturnsSameVariant()
    {
        var exp = _engine.CreateExperiment("test5", "desc", new[] { "a", "b" });
        _engine.StartExperiment(exp.Id);

        var v1 = _engine.AssignVariant(exp.Id, "session3");
        var v2 = _engine.AssignVariant(exp.Id, "session3");
        Assert.Equal(v1, v2);
    }

    [Fact]
    public void RecordConversion_UpdatesVariantStats()
    {
        var exp = _engine.CreateExperiment("test6", "desc", new[] { "a", "b" });
        _engine.StartExperiment(exp.Id);

        var variant = _engine.AssignVariant(exp.Id, "session4");
        _engine.RecordConversion(exp.Id, "session4", 0.9);

        var results = _engine.GetResults(exp.Id);
        var variantResult = results.Find(r => r.VariantName == variant);
        Assert.NotNull(variantResult);
        Assert.Equal(1, variantResult.Impressions);
        Assert.Equal(1, variantResult.Conversions);
        Assert.Equal(1.0, variantResult.ConversionRate);
        Assert.Equal(0.9, variantResult.AverageScore);
    }

    [Fact]
    public void GetResults_NoConversions_ReturnsZeroRates()
    {
        var exp = _engine.CreateExperiment("test7", "desc", new[] { "a", "b" });
        _engine.StartExperiment(exp.Id);

        var results = _engine.GetResults(exp.Id);
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(0.0, r.ConversionRate));
    }

    [Fact]
    public void GetStatus_ReturnsCorrectCounts()
    {
        _engine.CreateExperiment("draft1", "desc", new[] { "a" });
        var exp2 = _engine.CreateExperiment("running1", "desc", new[] { "a" });
        _engine.StartExperiment(exp2.Id);

        var status = _engine.GetStatus();
        Assert.Equal(2, status["total_experiments"]);
        Assert.Equal(1, status["running"]);
    }

    [Fact]
    public void GetResults_UnknownExperiment_ReturnsEmpty()
    {
        var results = _engine.GetResults("nonexistent");
        Assert.Empty(results);
    }
}
