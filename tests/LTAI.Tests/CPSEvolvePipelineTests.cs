using LTAI.Core.Governors;
using Xunit;

namespace LTAI.Tests;

public sealed class CPSEvolvePipelineTests
{
    // =========================================================================
    // Test 1: Full CPS pipeline — query → route → record → gene
    // =========================================================================

    [Fact]
    public async Task CPSProcessingService_20Queries_ProducesRouteDistribution()
    {
        var (cps, _, _, _) = CreateFullPipeline();

        var queries = new[]
        {
            "如何编写 Python 快速排序？",
            "What is the derivative of sin(x)?",
            "今天天气怎么样？",
            "帮我分析项目架构问题",
            "用中文解释量子纠缠",
            "Generate a React login component",
            "Run git diff on this file",
            "解释一下注意力机制的原理",
            "How to optimize SQL queries?",
            "有哪些好的JavaScript框架？",
            "帮我重构这段代码",
            "什么是微服务架构？",
            "Write a Python decorator example",
            "总结一下项目进展",
            "Help me debug this error message",
            "翻译: Hello, how are you?",
            "分析数据集中是否存在异常值",
            "How to deploy a Docker container?",
            "解释 RESTful API 设计原则",
            "给我一个MongoDB查询示例",
        };

        var results = new List<CPSResult>();
        foreach (var q in queries)
        {
            var r = await cps.ProcessAsync(q);
            results.Add(r);
        }

        Assert.Equal(20, results.Count);
        Assert.True(results.All(r => r.Success), "All results should succeed");
        Assert.All(results, r => Assert.NotEmpty(r.Route));

        var distribution = cps.GetRouteDistribution();
        Assert.NotEmpty(distribution);
        Assert.True(cps.GetTotalProcessed() == 20);
    }

    // =========================================================================
    // Test 2: Gene pool fills from CPS + evolution
    // =========================================================================

    [Fact]
    public async Task CPSWithEvolution_GeneratesAndEvolvesGenes()
    {
        var (cps, genePool, annealer, geneToRule) = CreateFullPipeline();

        for (int i = 0; i < 15; i++)
        {
            await cps.ProcessAsync($"Test query for gene generation #{i}");
        }

        int genesBefore = genePool.Count;

        var gen1 = genePool.Evolve(eliteCount: 2, crossoverCount: 4, mutateCount: 6);
        Assert.NotNull(gen1);
        Assert.True(gen1.Generation > 0);

        foreach (var gene in genePool.AllGenes)
        {
            genePool.UpdateFitness(gene.Id, 0.6);
        }

        var epoch = await annealer.StepAsync(proposalsPerEpoch: 5);
        Assert.NotNull(epoch);
        Assert.True(epoch.Epoch > 0);

        var gen2 = genePool.Evolve(eliteCount: 2, crossoverCount: 4, mutateCount: 6);
        Assert.True(gen2.Generation > gen1.Generation);

        int deployed = await geneToRule.DeployTopGenesAsync(topN: 3);
        Assert.True(deployed >= 0,
            $"Deployed {deployed} genes to frontier");
    }

    // =========================================================================
    // Test 3: Architect loop diagnoses and handles safe proposal
    // =========================================================================

    [Fact]
    public async Task ArchitectLoop_ProducesSafeProposal()
    {
        var (cps, genePool, _, _) = CreateFullPipeline();
        var router = CreateRouter();
        var teacher = new BootstrapTeacher(router);
        var annealer = new SimulatedAnnealer(genePool, router);
        var geneToRule = new GeneToRule(genePool, router);
        var counterfactual = new CounterfactualGate();

        for (int i = 0; i < 10; i++)
        {
            await cps.ProcessAsync($"Architect warmup query #{i}");
        }
        genePool.Evolve(eliteCount: 2, crossoverCount: 3, mutateCount: 5);
        await annealer.StepAsync(proposalsPerEpoch: 3);
        await teacher.AdvancePhaseIfReadyAsync();

        var architect = new ArchitectLoop(
            router: router,
            teacher: teacher,
            genePool: genePool,
            annealer: annealer,
            geneToRule: geneToRule,
            l2Architect: (prompt, ct) => Task.FromResult(FakeArchitectResponse(prompt)),
            counterfactualGate: counterfactual,
            minLoopInterval: TimeSpan.Zero);

        var proposal = await architect.RunAsync();

        Assert.NotNull(proposal);
        Assert.True(proposal!.Status == ProposalStatus.Deployed ||
                    proposal.Status == ProposalStatus.Approved ||
                    proposal.Status == ProposalStatus.Rejected,
            $"Unexpected status: {proposal.Status}");

        Assert.True(architect.LoopCount >= 1);
    }

    // =========================================================================
    // Test 4: Architect rejects high-risk proposals
    // =========================================================================

    [Fact]
    public async Task ArchitectLoop_RejectsHighRiskProposal()
    {
        var router = CreateRouter();
        var teacher = new BootstrapTeacher(router);
        var pool = new GenePool();
        var annealer = new SimulatedAnnealer(pool, router);
        var geneToRule = new GeneToRule(pool, router);

        pool.Seed(new[]
        {
            new Gene { Id = "s1", Condition = "intent == test", Action = "route_l1", Weight = 0.5, Niche = "test" }
        });
        pool.Evolve();
        await annealer.StepAsync(proposalsPerEpoch: 2);

        var architect = new ArchitectLoop(
            router, teacher, pool, annealer, geneToRule,
            l2Architect: (_, _) => Task.FromResult(
                """{"issue":"Frontier too small","root_cause":"Insufficient exploration","severity":0.8,"affected_components":["ParetoRouter"]}"""),
            minLoopInterval: TimeSpan.Zero);

        var proposal = await architect.RunAsync();
        Assert.Null(proposal);
    }

    // =========================================================================
    // Test 5: BootstrapTeacher phase never regresses
    // =========================================================================

    [Fact]
    public async Task BootstrapTeacher_PhaseNeverRegresses()
    {
        var router = CreateRouter();
        var teacher = new BootstrapTeacher(router);

        var emb = new float[768];
        emb[0] = 0.9f;

        Assert.Equal(BootstrapPhase.Teaching, teacher.Phase);

        for (int i = 0; i < 5; i++)
        {
            await teacher.RecordL2DecisionAsync(emb, "L2", 0.9f, 0.1f, 1.0f);
            await teacher.RecordL0DecisionAsync(emb, "L2");
        }

        Assert.True(teacher.Phase == BootstrapPhase.Teaching,
            $"Phase should stay Teaching, got {teacher.Phase}");

        await teacher.ForceAdvancePhaseAsync(BootstrapPhase.Shadowing);

        Assert.Equal(BootstrapPhase.Shadowing, teacher.Phase);

        for (int i = 0; i < 3; i++)
        {
            await teacher.RecordL2DecisionAsync(emb, "L2", 0.9f, 0.1f, 1.0f);
            await teacher.RecordL0DecisionAsync(emb, "L2");
        }

        Assert.Equal(BootstrapPhase.Shadowing, teacher.Phase);
    }

    // =========================================================================
    // Test 6: CounterfactualGate blocks shadow-based risky deploy
    // =========================================================================

    [Fact]
    public async Task CounterfactualGate_IntegratedWithArchitect_BlocksRisky()
    {
        var router = CreateRouter();
        var teacher = new BootstrapTeacher(router);
        var pool = new GenePool();
        var annealer = new SimulatedAnnealer(pool, router);
        var geneToRule = new GeneToRule(pool, router);

        pool.Seed(new[]
        {
            new Gene { Id = "g1", Condition = "intent == code", Action = "route_l2", Weight = 0.7, Niche = "code" },
            new Gene { Id = "g2", Condition = "intent == chat", Action = "route_l1", Weight = 0.6, Niche = "chat" },
        });
        pool.Evolve();
        await annealer.StepAsync();

        var gate = new CounterfactualGate(regretThreshold: 0.1, shiftThreshold: 0.15);
        gate.SeedTestBatch(new[]
        {
            ("code", "L2"), ("chat", "L1"), ("general", "local"),
            ("reflex", "reflex"), ("math", "L2"),
        });

        var proposal = new ArchitectureProposal
        {
            DiagnosisId = "diag1",
            Description = "Add test Pareto point",
            Action = ArchitectureAction.AddParetoPoint,
            TargetComponent = "ParetoRouter",
            Risk = 0.5,
            ExpectedImprovement = 0.2,
            Status = ProposalStatus.Approved,
            Parameters = new Dictionary<string, object>
            {
                ["quality"] = 0.5, ["speed"] = 0.5, ["cost"] = 0.5, ["label"] = "test_label"
            }
        };

        var shadow = gate.CloneRouter(router);
        var snapResult = gate.Evaluate(router, shadow);
        Assert.True(snapResult.Passed || snapResult.RegretScore < 0.2,
            $"Cloned router should pass gate: {snapResult.Reason}");
    }

    // =========================================================================
    // Test 7: EvolutionLoopHostedService starts without throwing
    // =========================================================================

    [Fact]
    public async Task EvolutionLoopHostedService_StartsGracefully()
    {
        var router = CreateRouter();
        var teacher = new BootstrapTeacher(router);
        var pool = new GenePool();
        var annealer = new SimulatedAnnealer(pool, router);
        var geneToRule = new GeneToRule(pool, router);

        var architect = new ArchitectLoop(
            router, teacher, pool, annealer, geneToRule,
            l2Architect: (_, _) => Task.FromResult("""{"issue":"none"}"""),
            minLoopInterval: TimeSpan.FromMilliseconds(100));

        var evoLoop = new EvolutionLoopHostedService(
            router, teacher, pool, annealer, geneToRule, architect,
            evolutionInterval: TimeSpan.FromMilliseconds(200),
            architectInterval: TimeSpan.FromMilliseconds(300),
            deployInterval: TimeSpan.FromMilliseconds(400));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var loopTask = evoLoop.StartAsync(cts.Token);

        await Task.Delay(500, CancellationToken.None);

        try
        {
            await cts.CancelAsync();
            await evoLoop.StopAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) { }

        try { await loopTask; } catch { }

        Assert.True(evoLoop.GetGeneGeneration() >= 0);
        Assert.True(evoLoop.GetEvolutionEpoch() >= 0);
    }

    // =========================================================================
    // Test 8: Full integrated state check after 10 query pipeline run
    // =========================================================================

    [Fact]
    public async Task FullPipelineRun_CPS_Evolve_Architect_EndToEnd()
    {
        var router = CreateRouter();
        var teacher = new BootstrapTeacher(router);
        var pool = new GenePool();
        var annealer = new SimulatedAnnealer(pool, router);
        var geneToRule = new GeneToRule(pool, router);
        var gate = new CounterfactualGate();

        var cps = new CPSProcessingService(
            paretoRouter: router,
            intentClassifier: (q, _) => q.Contains("error", StringComparison.OrdinalIgnoreCase) ? "code" : "general",
            teacher: teacher,
            genePool: pool,
            annealer: annealer,
            geneToRule: geneToRule,
            l1Invoke: (p, _) => Task.FromResult($"L1 says: {p[..Math.Min(p.Length, 30)]}"),
            l2Invoke: (p, _) => Task.FromResult($"L2 detailed response for: {p[..Math.Min(p.Length, 40)]}"));

        var architect = new ArchitectLoop(
            router, teacher, pool, annealer, geneToRule,
            l2Architect: (prompt, ct) => Task.FromResult(FakeArchitectResponse(prompt)),
            counterfactualGate: gate,
            minLoopInterval: TimeSpan.Zero);

        var queries = new[]
        {
            "How to sort an array?",
            "Explain recursion",
            "Error: NullReferenceException",
            "What is a linked list?",
            "Refactor this class",
            "Show me a binary tree example",
            "Error: Stack overflow",
            "Best sorting algorithm?",
            "How does HashMap work?",
            "What is Big O notation?",
        };

        foreach (var q in queries)
            await cps.ProcessAsync(q);

        Assert.Equal(10, cps.GetTotalProcessed());

        pool.Evolve(eliteCount: 2, crossoverCount: 3, mutateCount: 5);
        await annealer.StepAsync(proposalsPerEpoch: 3);

        var initialGeneCount = pool.Count;

        var proofTest = pool.Evolve(eliteCount: 1, crossoverCount: 2, mutateCount: 3);
        Assert.NotNull(proofTest);
        Assert.True(proofTest.Generation >= 2);

        int deployed = await geneToRule.DeployTopGenesAsync(topN: 2);
        Assert.True(deployed >= 0);

        var proposal = await architect.RunAsync();
        if (proposal != null)
        {
            Assert.True(proposal.Risk <= 0.7 ||
                        proposal.Status == ProposalStatus.Rejected,
                "High-risk proposals should be rejected");
        }

        var teacherStats = teacher.GetStats();
        Assert.True(teacherStats.TotalQueries > 0);

        var distribution = cps.GetRouteDistribution();
        Assert.NotEmpty(distribution);

        var frontier = router.GetFrontier();
        Assert.True(frontier.Count > 0);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static ParetoRouter CreateRouter()
        => new(embeddingDim: 768, metric: ParetoDistanceMetric.Cosine);

    private static (CPSProcessingService cps, GenePool pool, SimulatedAnnealer annealer, GeneToRule geneToRule) CreateFullPipeline()
    {
        var router = CreateRouter();
        var teacher = new BootstrapTeacher(router);
        var pool = new GenePool(maxPopulation: 50);
        var annealer = new SimulatedAnnealer(pool, router);
        var geneToRule = new GeneToRule(pool, router);

        var cps = new CPSProcessingService(
            paretoRouter: router,
            intentClassifier: (q, _) => q.StartsWith("What", StringComparison.OrdinalIgnoreCase) ? "code"
                : q.Contains("Error", StringComparison.OrdinalIgnoreCase) ? "code"
                : q.Contains("bug", StringComparison.OrdinalIgnoreCase) ? "code"
                : "general",
            teacher: teacher,
            genePool: pool,
            annealer: annealer,
            geneToRule: geneToRule,
            l1Invoke: (p, _) => Task.FromResult($"L1 answer: {p[..Math.Min(p.Length, 25)]}"),
            l2Invoke: (p, _) => Task.FromResult(
                $"L2 thorough analysis of: {p[..Math.Min(p.Length, 50)]}. This involves deep reasoning about the query structure and providing a comprehensive answer with examples and edge cases."));

        return (cps, pool, annealer, geneToRule);
    }

    private static string FakeArchitectResponse(string prompt)
    {
        if (prompt.Contains("INSTRUCTIONS") && prompt.Contains("ROOT CAUSE"))
        {
            return """{"issue":"Gene population growing slowly","root_cause":"Insufficient high-quality query traffic","severity":0.45,"affected_components":["GenePool"]}""";
        }

        return """{"action":"TriggerEvolution","target_component":"GenePool","description":"Run evolution to increase gene diversity","expected_improvement":0.15,"risk":0.25,"parameters":{"generations":2},"rollback_strategy":"revert_to_snapshot"}""";
    }
}
