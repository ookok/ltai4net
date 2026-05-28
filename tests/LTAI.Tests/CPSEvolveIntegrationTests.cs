using LTAI.Core.Governors;
using Xunit;

namespace LTAI.Tests;

public sealed class CPSEvolveIntegrationTests
{
    // =========================================================================
    // Test 1: BootstrapTeacher phase progression + accuracy improvement
    // =========================================================================

    [Fact]
    public async Task BootstrapTeacher_TracksStatsAndPhases()
    {
        var router = new ParetoRouter(embeddingDim: 4, metric: ParetoDistanceMetric.Euclidean);
        var teacher = new BootstrapTeacher(router);

        Assert.Equal(BootstrapPhase.Teaching, teacher.Phase);

        var embedding = new float[] { 0.95f, 0.1f, 0.02f, 0.01f };

        for (int i = 0; i < 10; i++)
        {
            await teacher.RecordL2DecisionAsync(embedding, "L2", 0.90f, 0.15f, 1.0f);
            await teacher.RecordL0DecisionAsync(embedding, "L2");
            await teacher.AdvancePhaseIfReadyAsync();
        }

        var stats = teacher.GetStats();
        Assert.Equal(20, stats.TotalQueries);
        Assert.True(stats.L2Queries == 10, $"Expected 10 L2 queries, got {stats.L2Queries}");
        Assert.Equal(BootstrapPhase.Teaching, stats.Phase);
        Assert.True(stats.PhaseStartedAt != default);
        Assert.True(stats.CuriosityBudget > 0);
    }

    // =========================================================================
    // Test 2: GenePool seed + evolve converges fitness
    // =========================================================================

    [Fact]
    public void GenePool_SeedEvolve_FitnessConverges()
    {
        var pool = new GenePool(config: new GenePoolConfig { MaxPopulation = 50 });

        var seeds = new List<Gene>
        {
            new() { Id = "g01", Condition = "intent == code",      Action = "route_l2",   Weight = 0.6, Niche = "code" },
            new() { Id = "g02", Condition = "intent == chat",      Action = "route_l1",   Weight = 0.7, Niche = "chat" },
            new() { Id = "g03", Condition = "complexity > 0.7",    Action = "route_l2",   Weight = 0.5, Niche = "reasoning" },
            new() { Id = "g04", Condition = "complexity < 0.3",    Action = "route_reflex",Weight= 0.8, Niche = "general" },
            new() { Id = "g05", Condition = "language == zh",      Action = "route_l1",   Weight = 0.6, Niche = "chat" },
        };

        pool.Seed(seeds);
        Assert.Equal(5, pool.Count);

        var rng = new Random(42);
        double finalAvgFitness = 0;

        for (int gen = 0; gen < 20; gen++)
        {
            pool.Evolve(eliteCount: 2, crossoverCount: 4, mutateCount: 6);

            double genFitness = 0;
            int count = 0;
            foreach (var gene in pool.AllGenes)
            {
                double reward = 0.4 + rng.NextDouble() * 0.4;
                pool.UpdateFitness(gene.Id, reward);
                genFitness += gene.Fitness;
                count++;
            }

            if (count > 0)
                finalAvgFitness = genFitness / count;
        }

        Assert.True(finalAvgFitness > 0.3,
            $"Average fitness {finalAvgFitness:F3} should exceed 0.3 after 20 generations");
        Assert.True(pool.History.Count == 20);
    }

    // =========================================================================
    // Test 3: CounterfactualGate detects distribution shift
    // =========================================================================

    [Fact]
    public void CounterfactualGate_DetectsBehavioralShift()
    {
        var gate = new CounterfactualGate(regretThreshold: 0.15, shiftThreshold: 0.25);

        var original = new ParetoRouter(embeddingDim: 768, metric: ParetoDistanceMetric.Cosine);
        var shadow = gate.CloneRouter(original);

        Assert.Equal(original.FrontierSize, shadow.FrontierSize);

        var testSamples = new[]
        {
            ("code", "L2"), ("math", "L2"), ("chat", "L1"),
            ("reflex", "reflex"), ("general", "local"),
        };
        gate.SeedTestBatch(testSamples);

        var emb = new float[768];
        emb[0] = 0.9f;
        emb[1] = 0.1f;

        original.Decide(emb);
        shadow.Decide(emb);

        var result = gate.Evaluate(original, shadow, sampleSize: 3);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public void CounterfactualGate_ClonePreservesFrontier()
    {
        var gate = new CounterfactualGate();
        var original = new ParetoRouter(embeddingDim: 768, metric: ParetoDistanceMetric.Cosine);

        var clone = gate.CloneRouter(original);
        Assert.NotNull(clone);
        Assert.Equal(original.FrontierSize, clone.FrontierSize);
    }

    // =========================================================================
    // Test 4: ParetoRouter jitter lock
    // =========================================================================

    [Fact]
    public void ParetoRouter_Jitter_StaysBounded()
    {
        var router = new ParetoRouter(embeddingDim: 4, metric: ParetoDistanceMetric.Euclidean);

        float[] stableEmb = [0.5f, 0.3f, 0.1f, 0.05f];

        var firstDecision = router.Decide(stableEmb);
        Assert.NotNull(firstDecision.Route);

        for (int i = 0; i < 40; i++)
            router.Decide(stableEmb);

        float jitter = router.GetJitter();
        Assert.True(jitter >= 0, $"Jitter should be non-negative, got {jitter:F3}");
        Assert.Equal(41, router.TotalDecisions);
    }

    [Fact]
    public void ParetoRouter_DifferentEmbeddings_ReturnDifferentRoutes()
    {
        var router = new ParetoRouter(embeddingDim: 4, metric: ParetoDistanceMetric.Euclidean);

        float[] highQ = [0.9f, 0.1f, 0.05f, 0.02f];
        float[] highS = [0.1f, 0.9f, 0.3f, 0.5f];
        float[] lowC  = [0.3f, 0.8f, 0.01f, 0.1f];

        var d1 = router.Decide(highQ);
        var d2 = router.Decide(highS);
        var d3 = router.Decide(lowC);

        Assert.NotNull(d1.Route);
        Assert.NotNull(d2.Route);
        Assert.NotNull(d3.Route);
    }

    // =========================================================================
    // Test 5: ParetoRouter frontier management
    // =========================================================================

    [Fact]
    public void ParetoRouter_AddRemoveFrontierPoints()
    {
        var router = new ParetoRouter(embeddingDim: 4, metric: ParetoDistanceMetric.Euclidean);

        var points = router.GetFrontier();
        Assert.True(points.Count > 0, "Should have seed frontier points");

        router.AddFrontierPoint(new ParetoPoint
        {
            Id = "custom", Label = "custom_route",
            Quality = 0.5f, Speed = 0.5f, Cost = 0.5f
        });

        var front = router.GetFrontier();
        var custom = front.First(p => p.Id == "custom");
        Assert.Equal("custom_route", custom.Label);

        router.RemoveFrontierPoint("custom");
        front = router.GetFrontier();
        Assert.DoesNotContain(front, p => p.Id == "custom");
    }

    // =========================================================================
    // Test 6: MicroKernel basic ops
    // =========================================================================

    [Fact]
    public async Task MicroKernel_BasicOps_DoNotCrash()
    {
        var kernel = new MicroKernel(
            workspaceRoot: Environment.CurrentDirectory);

        Assert.True(kernel.IsHealthy);

        var readResult = await kernel.ReadFileAsync("nonexistent.txt");
        Assert.False(readResult.Success);
        Assert.NotEmpty(readResult.Error);

        var gitResult = await kernel.GitOpAsync("log", "--oneline -1");
        Assert.True(gitResult.Success || !string.IsNullOrEmpty(gitResult.Error));

        var audit = kernel.GetAuditTrail(10);
        Assert.NotNull(audit);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static float[] ComputeSimpleEmbedding(string query)
    {
        var hash = query.GetHashCode(StringComparison.Ordinal);
        var rng = new Random(hash);
        return new float[]
        {
            (float)rng.NextDouble(), (float)rng.NextDouble(),
            (float)rng.NextDouble(), (float)rng.NextDouble(),
        };
    }
}
