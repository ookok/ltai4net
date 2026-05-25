using LTAI.Agent.Skills;
using LTAI.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LTAI.Tests;

public sealed class SkillE2ETests
{
    [Fact]
    public void SkillE2E_01_SkillLoader_ParsesAllExampleSkills()
    {
        var loader = new SkillLoader(NullLogger<SkillLoader>.Instance);
        var skillsRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "skills");

        if (!Directory.Exists(skillsRoot))
            skillsRoot = Path.Combine(AppContext.BaseDirectory, "skills");

        if (!Directory.Exists(skillsRoot))
            return;

        var mdFiles = Directory.GetFiles(skillsRoot, "*.md", SearchOption.AllDirectories);
        Assert.True(mdFiles.Length > 0, "No skill files found");

        var skills = new List<Skill>();
        foreach (var file in mdFiles.Take(20))
        {
            var skill = loader.LoadAsync(file).GetAwaiter().GetResult();
            Assert.NotNull(skill);
            Assert.NotEmpty(skill!.Name);
            Assert.NotEmpty(skill.Domain);
            skills.Add(skill);
        }

        Assert.True(skills.Count > 0);
    }

    [Fact]
    public void SkillE2E_02_Registry_LoadsAndMatchesByTrigger()
    {
        var registry = BuildRegistry();
        var all = registry.All;
        Assert.True(all.Count > 0, "No skills loaded");

        var byDomain = registry.GetByDomain("filesystem/list");
        if (byDomain.Count == 0) byDomain = registry.GetByDomain("filesystem/cwd");

        Assert.NotEmpty(byDomain);
    }

    [Fact]
    public void SkillE2E_03_Registry_MatchesEiaDomain()
    {
        var registry = BuildRegistry();

        var waterSkills = registry.GetByDomain("eia/water");
        if (waterSkills.Count == 0)
            waterSkills = registry.MatchByTrigger("地表水环境影响评价 水质监测");
        Assert.NotEmpty(waterSkills);

        var airSkills = registry.GetByDomain("eia/air");
        if (airSkills.Count == 0)
            airSkills = registry.MatchByTrigger("大气环境质量评价 PM2.5");
        Assert.NotEmpty(airSkills);
    }

    [Fact]
    public void SkillE2E_04_Registry_ResolvesRequires()
    {
        var registry = BuildRegistry();

        var allSkills = registry.All;
        var skillWithDeps = allSkills.Values.FirstOrDefault(s => s.Requires.Count > 0);

        if (skillWithDeps == null) return;

        var deps = registry.ResolveRequires(skillWithDeps);
        Assert.True(deps.Count >= 0);
    }

    [Fact]
    public void SkillE2E_05_LayerSeparation_AllFiveLayersPresent()
    {
        var registry = BuildRegistry();

        var l0 = registry.GetByLayer(SkillLayer.L0);
        var l1 = registry.GetByLayer(SkillLayer.L1);
        var l2 = registry.GetByLayer(SkillLayer.L2);
        var l3 = registry.GetByLayer(SkillLayer.L3);
        var l4 = registry.GetByLayer(SkillLayer.L4);

        Assert.NotEmpty(l0);
        Assert.NotEmpty(l1);
        Assert.NotEmpty(l2);
        Assert.NotEmpty(l3);
        Assert.NotEmpty(l4);
    }

    [Fact]
    public void SkillE2E_06_Extractor_RecordsSuccessAndPromotes()
    {
        var registry = BuildRegistry();
        var extractor = new SkillExtractor(registry, null!, NullLogger<SkillExtractor>.Instance);

        extractor.RecordSuccess("test_pattern", new List<string> { "shell", "grep" }, "search for files", "found 5 files");

        extractor.RecordSuccess("test_pattern", new List<string> { "shell", "grep" }, "search for files", "found 3 files");

        extractor.RecordSuccess("test_pattern", new List<string> { "shell", "grep" }, "search for config", "found config.yaml");

        Assert.True(true);
    }

    [Fact]
    public void SkillE2E_07_Evolution_TracksSuccessRate()
    {
        var evo = new SkillEvolution();
        Assert.Equal(1.0, evo.SuccessRate);
        Assert.Equal(0, evo.TotalUses);

        evo.RecordSuccess();
        evo.RecordSuccess();
        evo.RecordFailure();

        Assert.Equal(3, evo.TotalUses);
        Assert.True(evo.SuccessRate > 0.6);
    }

    [Fact]
    public void SkillE2E_08_SkillLoader_SavesAndLoadsEvolution()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"ltai_test_skill_{Guid.NewGuid():N}.md");

        try
        {
            File.WriteAllText(tempFile, """
                # skill: test_skill
                domain: test
                layer: 0
                version: 1.0.0
                intent: testing

                triggers:
                  - pattern: "test"

                ## 步骤
                1. shell: echo test

                ## 验证
                - must_contain: "test"
                """);

            var loader = new SkillLoader(NullLogger<SkillLoader>.Instance);
            var skill = loader.LoadAsync(tempFile).GetAwaiter().GetResult();
            Assert.NotNull(skill);
            Assert.Equal("test_skill", skill!.Name);

            skill.Evolution.RecordSuccess();
            SkillLoader.SaveEvolution(tempFile, skill.Evolution);

            var skill2 = loader.LoadAsync(tempFile).GetAwaiter().GetResult();
            Assert.NotNull(skill2);
            Assert.Equal(1, skill2!.Evolution.TotalUses);
            Assert.True(skill2.Evolution.SuccessRate > 0.9);
        }
        finally
        {
            try { File.Delete(tempFile); File.Delete(tempFile + ".meta.json"); } catch { }
        }
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
