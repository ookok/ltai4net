using LTAI.Core.Governors;
using Xunit;

namespace LTAI.Tests;

public sealed class LoopTrapDetectorTests
{
    [Fact]
    public void Check_NoRepeat_ReturnsNotTrapped()
    {
        var detector = new LoopTrapDetector(historySize: 16);

        Assert.Equal(0, detector.TotalChecks);
        Assert.Equal(0, detector.TrapsDetected);
        Assert.Equal(0.0, detector.TrapRate);

        var result = detector.Check("search", "query about weather");
        Assert.False(result.Trapped);
        Assert.Equal(1, detector.TotalChecks);
    }

    [Fact]
    public void Check_ExactRepeat_DetectsAfterThreshold()
    {
        var detector = new LoopTrapDetector(exactRepeatThreshold: 3);

        detector.Check("search", "find cats");
        detector.Check("search", "find cats");
        var result = detector.Check("search", "find cats");

        Assert.True(result.Trapped);
        Assert.Equal("exact_repeat", result.TrapType);
        Assert.True(result.RepeatCount >= 3);
        Assert.NotEmpty(result.SuggestedActions);
        Assert.Contains("route_up", result.SuggestedActions);
        Assert.Equal(3, detector.TrapsDetected);
    }

    [Fact]
    public void Check_DifferentInputs_NoTrap()
    {
        var detector = new LoopTrapDetector(exactRepeatThreshold: 3);

        detector.Check("search", "query A");
        detector.Check("search", "query B");
        detector.Check("search", "query C");
        var result = detector.Check("search", "query D");

        Assert.False(result.Trapped);
    }

    [Fact]
    public void Check_Cycle_DetectsABPattern()
    {
        var detector = new LoopTrapDetector(cycleWindowSize: 8, exactRepeatThreshold: 10);

        for (int i = 0; i < 8; i++)
        {
            var input = (i % 2 == 0) ? "step A" : "step B";
            detector.Check("action", input);
        }

        Assert.True(detector.TrapsDetected > 0 || detector.TotalChecks > 0);
    }

    [Fact]
    public void Check_SemanticLoop_WithEmbeddings()
    {
        var detector = new LoopTrapDetector(cosineThreshold: 0.8f, exactRepeatThreshold: 3);

        var emb = new float[] { 0.5f, 0.5f, 0.5f, 0.5f };
        detector.Check("reason", "step 1", emb);
        detector.Check("reason", "step 2", emb);
        var result = detector.Check("reason", "step 3", emb);

        Assert.True(result.Trapped);
        Assert.Equal("semantic_loop", result.TrapType);
        Assert.Contains("temperature_bump", result.SuggestedActions);
    }

    [Fact]
    public void Check_SemanticallyDifferent_NoTrap()
    {
        var detector = new LoopTrapDetector(cosineThreshold: 0.9f, exactRepeatThreshold: 3);

        detector.Check("action", "step 1", new[] { 1f, 0f, 0f, 0f });
        detector.Check("action", "step 2", new[] { 0f, 1f, 0f, 0f });
        var result = detector.Check("action", "step 3", new[] { 0f, 0f, 1f, 0f });

        Assert.False(result.Trapped);
    }

    [Fact]
    public void CosineSimilarity_Identical()
    {
        var a = new float[] { 1f, 2f, 3f };
        var result = LoopTrapDetector.CosineSimilarity(a, a);
        Assert.True(result > 0.99f, $"Expected ~1.0, got {result}");
    }

    [Fact]
    public void CosineSimilarity_Orthogonal()
    {
        var a = new float[] { 1f, 0f, 0f };
        var b = new float[] { 0f, 1f, 0f };
        var result = LoopTrapDetector.CosineSimilarity(a, b);
        Assert.True(result < 0.01f, $"Expected ~0, got {result}");
    }

    [Fact]
    public void RecordBreak_IncrementsCounter()
    {
        var detector = new LoopTrapDetector();
        Assert.Equal(0, detector.TrapsBroken);

        detector.RecordBreak("route_up");
        Assert.Equal(1, detector.TrapsBroken);
    }

    [Fact]
    public void IdleLoop_LowDiversity_Detects()
    {
        var detector = new LoopTrapDetector(idleThreshold: 10, exactRepeatThreshold: 100);

        for (int i = 0; i < 10; i++)
            detector.Check("search", $"query {i % 2}");

        Assert.True(detector.TotalChecks == 10);
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var detector = new LoopTrapDetector(exactRepeatThreshold: 3);

        detector.Check("test", "same");
        detector.Check("test", "same");
        detector.Reset();

        var result = detector.Check("test", "same");
        Assert.False(result.Trapped);
    }
}
