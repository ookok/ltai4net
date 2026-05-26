using LTAI.Agent.MAF;
using LTAI.Agent.Skills;
using LTAI.Agent.Skills.Runtime;
using LTAI.Core.Configuration;
using LTAI.Knowledge.Core;
using LTAI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LTAI.Tests;

public sealed class SkillBootstrapE2ETests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ltai_bts_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    [Fact]
    public void BTS_01_DecomposerBreaksDownComplexTask()
    {
        var tempDir = CreateTempDir();
        var loader = new SkillLoader(NullLogger<SkillLoader>.Instance);
        var registry = new SkillRegistry(loader, NullLogger<SkillRegistry>.Instance, tempDir);

        var fakeSteps = "1. 首先审计模块依赖关系并收集所有csproj引用\n2. 评估代码质量评分包括复杂度、重复率、测试覆盖率\n3. 进行安全性评估检查SQL注入、XSS、认证漏洞\n4. 整合分析结果生成最终审计报告";
        var fakeLlm = new FakeChatClient().AddRoute("拆解", _ => fakeSteps);

        var decomposer = new SkillAwareDecomposer(registry, fakeLlm);

        var complexTask = "审计整个项目架构并生成详细的评估报告文档。第一，需要深入分析所有模块之间的依赖关系和相关联的耦合程度。第二，全面评估代码质量评分包括复杂度指标和重复率检测。第三，进行安全性全面评估检查潜在的注入漏洞和认证缺陷。";
        Assert.True(decomposer.NeedsDecomposition(complexTask));

        Assert.False(decomposer.NeedsDecomposition("hello"));
        Assert.False(decomposer.NeedsDecomposition("quick refactor"));
        Assert.False(decomposer.NeedsDecomposition(""));

        var rounds = decomposer.DecomposeAsync(complexTask, "code").GetAwaiter().GetResult();
        Assert.NotNull(rounds);
        Assert.InRange(rounds.Count, 2, 5);

        foreach (var round in rounds)
        {
            Assert.True(round.Index > 0);
            Assert.False(string.IsNullOrWhiteSpace(round.Goal));
        }
    }

    [Fact]
    public void BTS_01b_DecomposerHeuristicFallbackOnLlmError()
    {
        var tempDir = CreateTempDir();
        var loader = new SkillLoader(NullLogger<SkillLoader>.Instance);
        var registry = new SkillRegistry(loader, NullLogger<SkillRegistry>.Instance, tempDir);

        var failingLlm = new ThrowingChatClient();
        var decomposer = new SkillAwareDecomposer(registry, failingLlm);

        var query = "审计整个项目架构。评估代码质量评分。进行安全性评估检查。整合分析结果生成最终报告。第四项是验证所有测试通过。";
        var rounds = decomposer.DecomposeAsync(query, "code").GetAwaiter().GetResult();

        Assert.NotNull(rounds);
        Assert.True(rounds.Count >= 1);
        Assert.All(rounds, r => Assert.False(string.IsNullOrWhiteSpace(r.Goal)));
    }

    [Fact]
    public async Task BTS_02_SkillRegistryCycle()
    {
        var tempDir = CreateTempDir();
        var skillMd = Path.Combine(tempDir, "test_eia.md");
        await File.WriteAllTextAsync(skillMd, """
            # skill: eia_water_quality
            domain: eia/water
            layer: 1
            version: 1.0.0
            intent: 地表水环境质量评价，包括水质监测、污染分析

            ## triggers
            - pattern: "地表水" (weight: 1.0)
            - pattern: "water quality" (weight: 0.9)
            - pattern: "水质监测" (weight: 1.0)
            - pattern: "水质评价" (weight: 0.8)

            ## tags
            - eia
            - water
            - 环境评价

            ## 步骤
            1. shell: collect water quality data
            2. shell: analyze pollution level

            ## 验证
            - must_contain: "水质指标"
            - pattern: "^##\\s+水质评价"
            """);

        var secondSkillMd = Path.Combine(tempDir, "test_air.md");
        await File.WriteAllTextAsync(secondSkillMd, """
            # skill: eia_air_quality
            domain: eia/air
            layer: 1
            version: 1.0.0
            intent: 大气环境质量评价，包括PM2.5监测、SO2分析

            ## requires
            - eia_water_quality

            ## triggers
            - pattern: "大气" (weight: 1.0)
            - pattern: "air quality" (weight: 0.9)

            ## tags
            - eia
            - air
            - 环境评价

            ## 步骤
            1. shell: monitor air pollutants
            2. shell: calculate AQI

            ## 验证
            - must_contain: "AQI"
            """);

        var loader = new SkillLoader(NullLogger<SkillLoader>.Instance);
        var registry = new SkillRegistry(loader, NullLogger<SkillRegistry>.Instance, tempDir);
        await registry.LoadAllAsync();

        Assert.True(registry.All.Count >= 2);

        var water = registry.Get("eia_water_quality");
        Assert.NotNull(water);

        var direct = new Skill
        {
            Name = "direct_match_test",
            Domain = "eia/water",
            Layer = SkillLayer.L0,
            Intent = "direct test",
            Triggers = new List<SkillTrigger> { new() { Pattern = "地表水", Weight = 1.0f } }
        };
        registry.Register(direct);
        var directMatches = registry.MatchByTrigger("地表水环境评价 水质监测");
        Assert.NotEmpty(directMatches);

        var air = registry.Get("eia_air_quality");
        Assert.NotNull(air);
        var deps = registry.ResolveRequires(air!);
        Assert.NotEmpty(deps);
        Assert.Contains(deps, s => s.Name == "eia_water_quality");

        var l0Skills = registry.GetByLayer(SkillLayer.L0);
        var l1Skills = registry.GetByLayer(SkillLayer.L1);
        Assert.NotEmpty(l1Skills);

        var manualSkill = new Skill
        {
            Name = "manual_test",
            Domain = "test",
            Layer = SkillLayer.L0,
            Intent = "manual test skill",
            Triggers = new List<SkillTrigger> { new() { Pattern = "manual_test_trigger", Weight = 1.0f } }
        };
        registry.Register(manualSkill);
        Assert.NotNull(registry.Get("manual_test"));
        Assert.NotEmpty(registry.GetByLayer(SkillLayer.L0));

        var promoted = registry.Promote("manual_test", SkillLayer.L1);
        Assert.NotNull(promoted);
        Assert.Equal(SkillLayer.L1, promoted!.Layer);
        Assert.DoesNotContain(registry.GetByLayer(SkillLayer.L0), s => s.Name == "manual_test");
        Assert.Contains(registry.GetByLayer(SkillLayer.L1), s => s.Name == "manual_test");
    }

    [Fact]
    public void BTS_03_EvolutionAutoPromote()
    {
        var skill = new Skill
        {
            Name = "auto_promote_test",
            Domain = "test",
            Layer = SkillLayer.L0,
            Intent = "evolution test",
            Version = "1.0.0"
        };

        Assert.Equal(SkillLayer.L0, skill.Layer);
        Assert.Equal(1.0, skill.Evolution.SuccessRate);
        Assert.Equal(0, skill.Evolution.TotalUses);

        for (var i = 0; i < 50; i++)
            skill.Evolution.RecordSuccess();
        for (var i = 0; i < 5; i++)
            skill.Evolution.RecordFailure();

        Assert.Equal(55, skill.Evolution.TotalUses);
        Assert.Equal(50, skill.Evolution.SuccessCount);
        Assert.Equal(5, skill.Evolution.FailureCount);

        var rate = skill.Evolution.SuccessRate;
        Assert.True(rate > 0.85, $"Expected > 0.85, got {rate}");

        var registry = new SkillRegistry(
            new SkillLoader(NullLogger<SkillLoader>.Instance),
            NullLogger<SkillRegistry>.Instance,
            CreateTempDir());

        var suggested = registry.SuggestLayer(rate, skill.Evolution.TotalUses);
        Assert.Equal(SkillLayer.L3, suggested);

        var suggestedL1 = registry.SuggestLayer(0.6, 5);
        Assert.Equal(SkillLayer.L1, suggestedL1);

        var promoted = skill.PromoteTo(SkillLayer.L3);
        Assert.Equal(SkillLayer.L3, promoted.Layer);
        Assert.Equal(1, promoted.Evolution.UpgradeGeneration);
        Assert.Equal("auto_promote_test", promoted.Evolution.UpgradedFrom);

        skill = skill with { VersionHistory = new List<SkillVersionEntry> { new() { Version = "1.0.0", Reason = "initial" } } };
        Assert.NotEmpty(skill.VersionHistory);
        Assert.Single(skill.VersionHistory);
    }

    [Fact]
    public async Task BTS_04_MemoryFileRetrieveByTrigger()
    {
        var tempDir = CreateTempDir();
        var dbPath = Path.Combine(tempDir, "test_kg.db");
        using var kg = new KnowledgeGraph(NullLogger<KnowledgeGraph>.Instance, dbPath: dbPath);

        var memoryMd = Path.Combine(tempDir, "eia_memory.md");
        await File.WriteAllTextAsync(memoryMd, """
            # memory: 水系评价经验
            id: water_experience_001
            domain: eia/water
            topic: 水系评价经验总结
            confidence: 0.90

            ## summary
            之前进行的多个工业园区环评项目中，我们积累了丰富的水系评价经验。

            ## facts
            - 地表水监测应采用GB3838-2002标准 (0.95)
            - COD指标在工业园区评价中最为关键 (0.92)
            - 水质采样频率应至少每月一次 (0.88)

            ## context
            适用于工业园区、化工项目的环评工作。重点监测COD、氨氮、总磷等指标。

            ## tags
            - [eia]
            - [water]
            - [工业环评]
            - [水质监测]

            ## triggers
            - pattern: "水质|地表水|水环境|COD|氨氮|总磷|water"
              weight: 1.0
            """);

        var loader = new MemoryFileLoader(NullLogger<MemoryFileLoader>.Instance);
        var service = new MemoryFilesService(loader, kg, NullLogger<MemoryFilesService>.Instance, tempDir);
        await service.LoadAllAsync();

        Assert.Equal(1, service.FileCount);

        var waterResults = service.RetrieveRelevant("对工业园区项目进行地表水水质监测评价", domain: "eia/water");
        Assert.NotEmpty(waterResults);
        Assert.Contains(waterResults, m => m.Name.Contains("水系"));

        var irrelevantResults = service.RetrieveRelevant("实现用户登录模块的JWT认证", domain: "code");
        Assert.DoesNotContain(irrelevantResults, m => m.Name.Contains("水系"));

        var byDomain = service.GetByDomain("eia/water");
        Assert.NotEmpty(byDomain);

        var byTag = service.GetByTag("水质监测");
        Assert.NotEmpty(byTag);
    }

    [Fact]
    public void BTS_05_PromptSelectBest()
    {
        var tempDir = CreateTempDir();
        var loader = new PromptLoader(NullLogger<PromptLoader>.Instance);
        var service = new PromptService(loader, NullLogger<PromptService>.Instance, tempDir);

        var codePrompt = new PromptFile
        {
            Name = "code_review_prompt",
            Domain = "code",
            Template = "Review: {{code}}",
            Triggers = new List<PromptTrigger>
            {
                new() { Pattern = "code review", Weight = 1.0f },
                new() { Pattern = "代码审查", Weight = 1.0f }
            },
            Tags = new List<string> { "code", "review" }
        };
        service.Register(codePrompt);

        var eiaPrompt = new PromptFile
        {
            Name = "eia_report_prompt",
            Domain = "eia",
            Template = "EIA: {{project}}",
            Triggers = new List<PromptTrigger>
            {
                new() { Pattern = "环评", Weight = 1.0f },
                new() { Pattern = "EIA", Weight = 1.0f }
            },
            Tags = new List<string> { "eia", "report" }
        };
        service.Register(eiaPrompt);

        Assert.Equal(2, service.PromptCount);
        Assert.NotEmpty(service.GetByDomain("code"));

        var codeResults = service.SelectBest("请帮我做一次代码审查", domain: "code");
        Assert.NotEmpty(codeResults);

        var eiaResults = service.SelectBest("生成环评报告", domain: "eia");
        Assert.NotEmpty(eiaResults);

        var bestCode = service.GetBestForTask("做code review和代码审查", domain: "code");
        Assert.NotNull(bestCode);
        Assert.Contains("review", bestCode!.Name, StringComparison.OrdinalIgnoreCase);

        var bestEia = service.GetBestForTask("EIA环评报告", domain: "eia");
        Assert.NotNull(bestEia);
        Assert.Contains("eia", bestEia!.Name, StringComparison.OrdinalIgnoreCase);

        var allByDomain = service.GetByDomain("code");
        Assert.NotEmpty(allByDomain);

        var rendered = service.Render(codePrompt.Id, new Dictionary<string, string>
        {
            ["code"] = "public class Test {}"
        });
        Assert.True(rendered.Success);
        Assert.Contains("public class Test {}", rendered.Rendered);

        Assert.Equal(2, service.PromptCount);
    }

    [Fact]
    public void BTS_06_OptionServiceResolveChain()
    {
        var tempDir = CreateTempDir();
        var envVarName = $"LTAI_BTS_TEST_VAR_{Guid.NewGuid():N}";

        try
        {
            Environment.SetEnvironmentVariable(envVarName, "env_value_from_os");

            var loader = new OptionLoader(NullLogger<OptionLoader>.Instance);
            var defaults = new LTAIOptions();
            var configBuilder = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["BTS_TestKey_Config"] = "config_value_from_builder"
                });
            var config = configBuilder.Build();
            var service = new OptionService(loader, defaults, config, NullLogger<OptionService>.Instance, tempDir);

            var section = new OptionFile
            {
                Name = "bts_test_section",
                Section = "BTS",
                Description = "Bootstrap test section",
                Keys = new List<OptionKey>
                {
                    new() { Name = "env_resolved_key", EnvVar = envVarName, Description = "resolved from env" },
                    new() { Name = "BTS_TestKey_Config", Description = "resolved from config" },
                    new() { Name = "default_only_key", Default = "default_fallback_value", Description = "uses default" },
                    new() { Name = "expanded_key", Default = "prefix_{{ENV_EXPAND_TEST}}", Description = "expands vars" }
                }
            };
            service.Register(section.Name, section);

            var envVal = service.Resolve("bts_test_section", "env_resolved_key");
            Assert.Equal("env_value_from_os", envVal);

            var configVal = service.Resolve("bts_test_section", "BTS_TestKey_Config");
            Assert.Equal("config_value_from_builder", configVal);

            var defaultVal = service.Resolve("bts_test_section", "default_only_key");
            Assert.Equal("default_fallback_value", defaultVal);

            Environment.SetEnvironmentVariable("ENV_EXPAND_TEST", "EXPANDED");
            try
            {
                var expandedVal = service.Resolve("bts_test_section", "expanded_key");
                Assert.NotNull(expandedVal);
                Assert.True(expandedVal!.Contains("EXPANDED") || expandedVal.Contains("ENV_EXPAND_TEST"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("ENV_EXPAND_TEST", null);
            }

            var staticGet = OptionService.Get(envVarName);
            Assert.Equal("env_value_from_os", staticGet);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVarName, null);
        }
    }

    [Fact]
    public async Task BTS_07_SystemPromptAssemblerFullChain()
    {
        var tempDir = CreateTempDir();
        File.WriteAllText(Path.Combine(tempDir, "AGENTS.md"), "# Test Agent Spec\nrole: for E2E testing");

        var skillMd = Path.Combine(tempDir, "l0_test_skill.md");
        await File.WriteAllTextAsync(skillMd, """
            # skill: l0_test_skill
            domain: test
            layer: 0
            version: 1.0.0
            intent: A test skill for bootstrap verification

            triggers:
              - pattern: "test|bootstrap|验证"
                weight: 1.0

            ## 步骤
            1. shell: echo test passed

            ## 验证
            - must_contain: "test"
            """);

        var loader = new SkillLoader(NullLogger<SkillLoader>.Instance);
        var registry = new SkillRegistry(loader, NullLogger<SkillRegistry>.Instance, tempDir);
        await registry.LoadAllAsync();

        var assembler = new SystemPromptAssembler(registry);

        var ctx = new PromptLayerContext
        {
            WorkspaceRoot = tempDir,
            CurrentDir = tempDir,
            Platform = "win32",
            Date = "2026-05-26",
            Shell = "pwsh",
            GitBranch = "main",
            GitClean = true,
            BuildOk = true,
            Domain = "test",
            ModeHint = "This is a test bootstrap mode",
            TaskInstructions = "Verify the full skill bootstrap chain end-to-end",
            BuildDiagnostics = "Build: OK, no errors",
            MemoryContext = "Previous context: bootstrap test memory"
        };

        var assembled = assembler.Assemble(ctx);

        Assert.Contains("[AGENTS.md", assembled);
        Assert.Contains("Test Agent Spec", assembled);
        Assert.Contains("[Mode]", assembled);
        Assert.Contains("test bootstrap mode", assembled);
        Assert.Contains("[Environment]", assembled);
        Assert.Contains(tempDir, assembled);
        Assert.Contains("win32", assembled);
        Assert.Contains("pwsh", assembled);
        Assert.Contains("main", assembled);
        Assert.Contains("[Skills]", assembled);
        Assert.Contains("l0_test_skill", assembled);
        Assert.Contains("L0 Atomic", assembled);
        Assert.Contains("[Task]", assembled);
        Assert.Contains("bootstrap chain", assembled);
        Assert.Contains("[Diagnostics]", assembled);
        Assert.Contains("Build: OK", assembled);
        Assert.Contains("[Memory]", assembled);
        Assert.Contains("bootstrap test memory", assembled);

        var noSkillsCtx = new PromptLayerContext
        {
            WorkspaceRoot = tempDir,
            SuppressSkills = true
        };
        var suppressed = assembler.Assemble(noSkillsCtx);
        Assert.DoesNotContain("[Skills]", suppressed);

        assembler.InvalidateAgentsMdCache();
        var reloaded = assembler.Assemble(ctx);
        Assert.Contains("Test Agent Spec", reloaded);
    }
}

internal sealed class ThrowingChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        => throw new InvalidOperationException("Simulated LLM failure");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public void Dispose() { }
    public object? GetService(Type t, object? k = null) => null;
}
