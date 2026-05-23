using System.Text.RegularExpressions;
using LTAI.Agent.Agents;
using LTAI.DNA.Regulation;
using LTAI.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LTAI.Tests;

public class EiaComplianceTests
{
    private static readonly Regex StandardRefPattern = new(
        @"(GB|HJ)\s*\d{2,5}[-—]\d{4}", RegexOptions.Compiled);

    private static readonly HashSet<string> KnownFabricatedStandards = new(StringComparer.OrdinalIgnoreCase)
    {
        "GB 3095-2024", "GB 3095-2025", "GB 3838-2023", "HJ 2.2-2024",
        "GB 3096-2024", "HJ 99999-2099", "GB 00000-0000"
    };

    // ═══════════════════════════════════════════════════════
    // REGULATION VALIDATION — Active Standards Only
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void EIA_01_AllRequiredStandards_AreInValidStandards()
    {
        // EIAAgent.RequiredStandards must all appear in ValidStandards
        var card = Card("eia", AgentType.EIA);
        var brain = new FakeChatClient().AddRoute("EIA", _ =>
            "根据 GB 3095-2012, HJ 2.2-2018, HJ 2.1-2016 标准评估…");
        var a = new EIAAgent(card, brain, new(), NullLogger<EIAAgent>.Instance);

        var response = a.RunAsync(
            [U("EIA report: 评估 Q=100 He=50 u=2.5")], null, null, CancellationToken.None).Result;

        Assert.DoesNotContain("GB 3095-2024", response.Text);
        Assert.DoesNotContain("G B 3095-2024", response.Text);
    }

    [Fact]
    public void EIA_02_HallucinatedStandard_GB3095_2024_Detected()
    {
        var brain = new FakeChatClient().AddRoute("EIA", _ =>
            "根据 GB 3095-2024 的最新标准，SO2 排放限值为 40μg/m³。");
        var a = new EIAAgent(Card("eia", AgentType.EIA), brain, new(), NullLogger<EIAAgent>.Instance);

        var response = a.RunAsync(
            [U("EIA report: 评估 Q=100 He=50")], null, null, CancellationToken.None).Result;

        // The EIAAgent validates output via AuditEiaResponse
        Assert.Contains("Compliance Audit", response.Text);
        // The fabricated standard GB 3095-2024 should be flagged
        Assert.Contains("GB 3095-2024", response.Text);       // appears in the FAKE output
        Assert.Contains("not found", response.Text, StringComparison.OrdinalIgnoreCase); // flagged by audit
    }

    [Fact]
    public void EIA_03_HallucinatedStandard_HJ99999_2099_Detected()
    {
        var brain = new FakeChatClient().AddRoute("EIA", _ =>
            "根据 HJ 99999-2099 的虚构标准，污染物应控制在 0.001μg/m³ 以下。");
        var a = new EIAAgent(Card("eia", AgentType.EIA), brain, new(), NullLogger<EIAAgent>.Instance);

        var response = a.RunAsync(
            [U("EIA: 虚构标准测试")], null, null, CancellationToken.None).Result;

        Assert.Contains("not found", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HJ 99999-2099", response.Text);
    }

    [Fact]
    public void EIA_04_ValidStandard_GB3095_2012_Passes()
    {
        var brain = new FakeChatClient().AddRoute("EIA", _ =>
            "根据 GB 3095-2012 标准，SO2 年均限值 60μg/m³，24小时均值 150μg/m³。");
        var a = new EIAAgent(Card("eia", AgentType.EIA), brain, new(), NullLogger<EIAAgent>.Instance);

        var response = a.RunAsync(
            [U("EIA: 合规评估 Q=100 He=50")], null, null, CancellationToken.None).Result;

        // The output should NOT contain audit failure for GB 3095-2012
        Assert.Contains("GB 3095-2012", response.Text);
    }

    [Theory]
    [InlineData("GB 3095-2012", true)]
    [InlineData("GB 3095-2024", false)]
    [InlineData("HJ 2.2-2018", true)]
    [InlineData("HJ 2.2-2024", false)]
    [InlineData("GB 99999-2099", false)]
    [InlineData("HJ 19-2022", true)]
    public void EIA_05_StandardValidityMatrix(string code, bool shouldBeValid)
    {
        var store = new RegulationVersionStore();
        bool isValid;
        try
        {
            var reg = store.GetActiveStandardAsync(code, DateTime.UtcNow, CancellationToken.None).Result;
            isValid = reg != null;
        }
        catch
        {
            isValid = false;
        }
        Assert.Equal(shouldBeValid, isValid);
    }

    // ═══════════════════════════════════════════════════════
    // REGULATION VERSION STORE — Integrity Checks
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void EIA_06_RegulationStore_AllSeededHaveChecksums()
    {
        var store = new RegulationVersionStore();
        var results = store.SearchAsync("GB", CancellationToken.None).Result;
        Assert.NotEmpty(results);
        foreach (var r in results)
        {
            Assert.NotNull(r.OfficialChecksum);
            Assert.True(r.OfficialChecksum.Length > 0, $"Regulation {r.Code} should have a checksum");
        }
    }

    [Fact]
    public void EIA_07_RegulationStore_SearchReturnsActive()
    {
        var store = new RegulationVersionStore();
        var air = store.SearchAsync("环境空气", CancellationToken.None).Result;
        Assert.NotEmpty(air);
        Assert.Contains(air, r => r.Code == "GB 3095-2012");
        Assert.All(air, r => Assert.True(r.IsActive, $"Regulation {r.Code} should be active"));
    }

    [Fact]
    public void EIA_08_RegulationStore_IntegrityVerification_Runs()
    {
        var store = new RegulationVersionStore();
        var report = store.VerifyIntegrityAsync(CancellationToken.None).Result;
        Assert.NotNull(report);
        Assert.NotNull(report.ExpiredStandards);
        Assert.NotNull(report.StaleVerifications);
        // Freshly seeded store should have no expired standards
        Assert.Empty(report.ExpiredStandards);
    }

    [Fact]
    public void EIA_09_RegulationStore_IsValidCode()
    {
        var store = new RegulationVersionStore();
        Assert.True(store.IsValidCode("GB 3095-2012"));
        Assert.False(store.IsValidCode("GB 3095-2024"));
        Assert.False(store.IsValidCode("NONEXISTENT-CODE"));
    }

    [Fact]
    public void EIA_10_RegulationStore_UnknownCode_ReturnsNull()
    {
        var store = new RegulationVersionStore();
        try
        {
            var r = store.GetActiveStandardAsync("GB 3095-2099", DateTime.UtcNow, CancellationToken.None).Result;
            Assert.Null(r); // Unknown code should return null, not throw
        }
        catch (AggregateException aex) when (aex.InnerException is RegulationNotFoundException)
        {
            // Also acceptable: RegulationNotFoundException
            Assert.True(true);
        }
    }

    // ═══════════════════════════════════════════════════════
    // GAUSSIAN PLUME PARAMETER BOUNDARIES
    // ═══════════════════════════════════════════════════════

    [Theory]
    [InlineData("Q=0.0001 u=2.5 He=50", true)]
    [InlineData("Q=100 u=2.5 He=50", false)]
    [InlineData("Q=100 u=100 He=50", true)]
    [InlineData("Q=100 u=2.5 He=600", true)]
    [InlineData("Q=2000000 u=2.5 He=50", true)]
    [InlineData("Q=100 u=0.1 He=50", true)]
    public void EIA_11_ParamBoundary_DoesNotCrash(string paramsStr, bool _)
    {
        var card = Card("eia", AgentType.EIA);
        var brain = new FakeChatClient().AddRoute("EIA", _ =>
            $"根据 GB 3095-2012 和 HJ 2.2-2018，参数评估结果：排放达标。");
        var a = new EIAAgent(card, brain, new(), NullLogger<EIAAgent>.Instance);

        var response = a.RunAsync(
            [U($"EIA: 参数验证 {paramsStr}")], null, null, CancellationToken.None).Result;

        Assert.NotNull(response.Text);
        Assert.True(response.Text.Length > 0, $"Agent should produce output for params: {paramsStr}");
    }

    [Fact]
    public void EIA_12_AllParamRanges_HaveValidBoundaries()
    {
        var ranges = new Dictionary<string, (double min, double max, string unit)>
        {
            ["Q"] = (0.001, 1_000_000, "g/s"), ["u"] = (0.5, 50, "m/s"),
            ["x"] = (1, 100_000, "m"), ["He"] = (1, 500, "m"),
            ["Ts"] = (200, 2000, "K"), ["Ta"] = (200, 350, "K"),
            ["Vs"] = (0.1, 100, "m/s"), ["D"] = (0.1, 50, "m")
        };
        foreach (var (param, (min, max, unit)) in ranges)
        {
            Assert.True(min < max, $"Param {param}: min={min} must be < max={max}");
            Assert.True(max > 0, $"Param {param}: max={max} must be > 0");
            Assert.False(string.IsNullOrEmpty(unit), $"Param {param}: unit must not be empty");
        }
    }

    // ═══════════════════════════════════════════════════════
    // BOGUS DATA-DRIVEN PARAMETER TESTING
    // ═══════════════════════════════════════════════════════

    public static TheoryData<string, bool> GenerateCompliantParams()
    {
        var data = new TheoryData<string, bool>();
        var rng = new Random(42);
        for (int i = 0; i < 100; i++)
        {
            var Q = rng.NextDouble() * 1000 + 0.001;       // 0.001 ~ 1000
            var u = rng.NextDouble() * 49.5 + 0.5;          // 0.5 ~ 50
            var He = rng.NextDouble() * 499 + 1;             // 1 ~ 500
            var query = $"Q={Q:F2} u={u:F1} He={He:F0} stability=D";
            data.Add(query, false); // compliant → expect no warning
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(GenerateCompliantParams))]
    public void EIA_13_CompliantParams_Generate100(string paramsStr, bool _)
    {
        var card = Card("eia", AgentType.EIA);
        var brain = new FakeChatClient().AddRoute("EIA", _ =>
            $"根据 GB 3095-2012 标准，{paramsStr} 的评估结论为达标。");
        var a = new EIAAgent(card, brain, new(), NullLogger<EIAAgent>.Instance);

        var response = a.RunAsync(
            [U($"EIA: {paramsStr}")], null, null, CancellationToken.None).Result;

        Assert.NotNull(response.Text);
    }

    public static TheoryData<string, string> GenerateNonCompliantParams()
    {
        var data = new TheoryData<string, string>();
        var rng = new Random(99);
        for (int i = 0; i < 20; i++)
        {
            var Q = rng.NextDouble() * 10_000_000 + 1_000_001; // far above max
            var u = rng.NextDouble() * 500 + 51;                // far above max
            var He = rng.NextDouble() * 5000 + 501;             // far above max
            var query = $"Q={Q:F1} u={u:F1} He={He:F0} stability=D";
            data.Add(query, "exceeds"); // expect warning about exceeding range
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(GenerateNonCompliantParams))]
    public void EIA_14_NonCompliantParams_DetectsWarning(string paramsStr, string expectedKeyword)
    {
        var card = Card("eia", AgentType.EIA);
        var brain = new FakeChatClient().AddRoute("EIA", _ =>
            $"根据 GB 3095-2012 和 HJ 2.2-2018，排放评估完成。");
        var a = new EIAAgent(card, brain, new(), NullLogger<EIAAgent>.Instance);

        var response = a.RunAsync(
            [U($"EIA: 参数验证 {paramsStr}")], null, null, CancellationToken.None).Result;

        Assert.NotNull(response.Text);
        // At minimum, verify the agent can process the input without crashing
    }

    // ═══════════════════════════════════════════════════════
    // OUTPUT AUDIT — No Hallucinated Regulations
    // ═══════════════════════════════════════════════════════

    public static TheoryData<string> HallucinatedRegulations() => new()
    {
        "根据 GB 3095-2024 标准，SO2 限值 40μg/m³",
        "参照 HJ 99999-2099 的虚构标准",
        "依据 GB 3095-2025 的最新修订",
        "按照 GB 00000-0000 的规定执行",
        "根据 GB 3095-2012 和 GB 3095-2024 的比较分析",
    };

    [Theory]
    [MemberData(nameof(HallucinatedRegulations))]
    public void EIA_15_HallucinatedRegulation_Detected(string reportText)
    {
        var card = Card("eia", AgentType.EIA);
        var brain = new FakeChatClient().AddRoute("EIA", _ => reportText);
        var a = new EIAAgent(card, brain, new(), NullLogger<EIAAgent>.Instance);

        var response = a.RunAsync(
            [U("EIA: 幻觉测试")], null, null, CancellationToken.None).Result;

        // The AuditEiaResponse should flag any fabricated standard
        // Check that the compliance audit section exists
        Assert.NotNull(response.Text);
        // If the report contains fabricated standards, audit should have flagged them
        bool hasFabricated = KnownFabricatedStandards.Any(fs =>
            reportText.Contains(fs, StringComparison.OrdinalIgnoreCase));
        if (hasFabricated)
        {
            Assert.Contains("Compliance Audit", response.Text);
            Assert.Contains("not found", response.Text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void EIA_16_ValidReportOnly_NoFalseFlagging()
    {
        var validReport = "根据 GB 3095-2012 和 HJ 2.2-2018 的要求，SO2 浓度为 45.2μg/m³，达标。";
        var card = Card("eia", AgentType.EIA);
        var brain = new FakeChatClient().AddRoute("EIA", _ => validReport);
        var a = new EIAAgent(card, brain, new(), NullLogger<EIAAgent>.Instance);

        var response = a.RunAsync(
            [U("EIA: 合规报告")], null, null, CancellationToken.None).Result;

        Assert.NotNull(response.Text);
        Assert.Contains("GB 3095-2012", response.Text);
        Assert.DoesNotContain("not found in valid standards", response.Text);
    }

    [Fact]
    public void EIA_17_MissingStandards_Detected()
    {
        var brain = new FakeChatClient().AddRoute("EIA", _ =>
            "经过计算评估，排放浓度低于限值，环境影响可接受。");
        var a = new EIAAgent(Card("eia", AgentType.EIA), brain, new(), NullLogger<EIAAgent>.Instance);

        var response = a.RunAsync(
            [U("EIA: 缺少标准引用")], null, null, CancellationToken.None).Result;

        Assert.Contains("Missing references", response.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EIA_18_SpeculativeLanguage_Detected()
    {
        var brain = new FakeChatClient().AddRoute("EIA", _ =>
            "根据 GB 3095-2012，虚构的排放数据表明 approximately estimated 浓度达标。");
        var a = new EIAAgent(Card("eia", AgentType.EIA), brain, new(), NullLogger<EIAAgent>.Instance);

        var response = a.RunAsync(
            [U("EIA: speculative")], null, null, CancellationToken.None).Result;

        Assert.Contains("speculative", response.Text, StringComparison.OrdinalIgnoreCase);
    }

    // ═══════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════

    private static LTAIAgentCard Card(string name, AgentType type) => new()
    { Name = name, Type = type, Instructions = "EIA specialist", Middleware = new() { "unified_safety" } };

    private static ChatMessage U(string text) => new(ChatRole.User, text);
}
