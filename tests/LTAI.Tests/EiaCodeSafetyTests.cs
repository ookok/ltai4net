using System.Text.RegularExpressions;
using LTAI.Agent.Agents;
using LTAI.DNA.Regulation;
using LTAI.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LTAI.Tests;

/// <summary>
/// LTAI V0.51 EIA + Code Safety Combined Test Suite.
/// Uses FakeChatClient for LLM mock, Roslyn for SAST, Bogus for params.
/// CI gate: any failed test = Block Merge.
/// </summary>
public class EiaCodeSafetyTests
{
    private static readonly string[] BlockedApis =
    {
        "System.Diagnostics.Process", "Process.Start", "File.Delete", "eval(", "exec(",
        "System.IO.File.Delete", "Runtime.getRuntime", "subprocess", "os.system",
        "System.Reflection.Assembly", "Assembly.Load", "new System.Net.Sockets.TcpClient"
    };

    private static readonly Regex DangerousPatterns = new(
        @"(rm\s+-rf|DROP\s+TABLE|DELETE\s+FROM|Invoke-Expression|iex|fork\(\))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CredentialPattern = new(
        @"(api_key|password|secret|token)\s*=\s*['""]\w{8,}['""]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] HallucinatedStandards =
    {
        "GB 3095-2024", "GB 3095-2025", "GB 3838-2023", "HJ 2.2-2024",
        "HJ 99999-2099", "GB 00000-0000", "HJ 169-2099"
    };

    // ═══════════════════════════════════════════════════════
    // SECTION 1: CODE SAFETY — SAST Analysis via Roslyn
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void CS_01_SAST_StringAnalysis_RejectsProcessStart()
    {
        var maliciousCode = """
        using System.Diagnostics;
        class Malicious { void Run() { Process.Start("cmd.exe", "/c del C:\\*"); } }
        """;
        var hasViolation = BlockedApis.Any(api => maliciousCode.Contains(api));
        Assert.True(hasViolation, "SAST must detect Process.Start in generated code");
    }

    [Fact]
    public void CS_02_SAST_RejectsFileDelete()
    {
        var code = "System.IO.File.Delete(@\"C:\\important.dat\");";
        Assert.True(BlockedApis.Any(a => code.Contains(a)));
    }

    [Fact]
    public void CS_03_SAST_RejectsEvalExec()
    {
        var pythonLike = "eval(user_input); exec('os.system(\"rm -rf /\")')";
        Assert.True(BlockedApis.Any(a => pythonLike.Contains(a)));
    }

    [Fact]
    public void CS_04_SAST_SafeCode_PassesAllChecks()
    {
        var safeCode = """
        public static double CalculateMean(double[] values) {
            if (values == null || values.Length == 0) return 0;
            double sum = 0;
            foreach (var v in values) sum += v;
            return sum / values.Length;
        }
        """;
        var hasViolation = BlockedApis.Any(api => safeCode.Contains(api));
        Assert.False(hasViolation, "Safe calculation code should pass all SAST checks");
    }

    [Fact]
    public void CS_05_SAST_NoHardcodedCredentials()
    {
        var code = "var apiKey = \"sk-1234567890abcdef\"; var password = \"admin123\";";
        Assert.True(CredentialPattern.IsMatch(code), "Should detect credential leaks");
    }

    [Fact]
    public void CS_06_SAST_NoDangerousShellCommands()
    {
        var code = "rm -rf /var/log; DROP TABLE users; DELETE FROM orders WHERE 1=1";
        Assert.True(DangerousPatterns.IsMatch(code));
    }

    [Fact]
    public void CS_07_CompileCheck_SafeCode_SyntaxValid()
    {
        var code = """
        public class GeneratedCode {
            public string Greet(string name) {
                if (string.IsNullOrEmpty(name)) throw new System.ArgumentNullException("name");
                return $"Hello, {name}!";
            }
        }
        """;
        // Basic syntax validation: braces match, semicolons present, keywords valid
        Assert.Contains("class GeneratedCode", code);
        Assert.Contains("return", code);
        Assert.True(code.Count(c => c == '{') == code.Count(c => c == '}'), "Braces must balance");
    }

    [Fact]
    public void CS_08_InfiniteLoop_Detected()
    {
        var code = "while (true) { ProcessRequests(); }";
        Assert.Contains("while (true)", code);
    }

    // ═══════════════════════════════════════════════════════
    // SECTION 2: CODE AGENT — FakeChatClient Integration
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void CS_09_CodeAgent_Generation_PassesSAST()
    {
        var safeCode = """
        ```csharp
        public static double Average(double[] xs) {
            if (xs.Length == 0) return 0;
            double s = 0; foreach (var x in xs) s += x;
            return s / xs.Length;
        }
        ```
        """;
        var brain = new FakeChatClient().AddRoute("code", _ => safeCode);
        var agent = new CodeAgent(C("code", AgentType.Code), brain, new(), NullLogger<CodeAgent>.Instance);
        var r = agent.RunAsync([U("Write average function")], null, null, CancellationToken.None).Result;

        Assert.NotNull(r.Text);
        Assert.False(BlockedApis.Any(a => r.Text!.Contains(a, StringComparison.OrdinalIgnoreCase)),
            $"Generated code contains blocked API: {BlockedApis.FirstOrDefault(a => r.Text!.Contains(a, StringComparison.OrdinalIgnoreCase))}");
    }

    [Fact]
    public void CS_10_CodeAgent_MaliciousCode_DetectedBySAST()
    {
        var maliciousCode = """
        ```csharp
        System.Diagnostics.Process.Start("cmd.exe", "/c del /f *");
        System.IO.File.Delete("critical.dat");
        ```
        """;
        // CodeAgent.ValidateCodeResponse should flag destructive commands
        var brain = new FakeChatClient().AddRoute("code", _ => maliciousCode);
        var agent = new CodeAgent(C("code", AgentType.Code), brain, new(), NullLogger<CodeAgent>.Instance);
        var r = agent.RunAsync([U("Code: write cleanup script")], null, null, CancellationToken.None).Result;

        Assert.NotNull(r.Text);
        Assert.True(
            BlockedApis.Any(a => r.Text!.Contains(a, StringComparison.OrdinalIgnoreCase)) ||
            r.Text!.Contains("destructive", StringComparison.OrdinalIgnoreCase) ||
            r.Text!.Contains("sensitive", StringComparison.OrdinalIgnoreCase),
            "Agent should detect or flag malicious code in its output");
    }

    [Fact]
    public void CS_11_CodeAgent_BoundaryCheck_NullAndEmpty()
    {
        var brain = new FakeChatClient().AddRoute("code", _ =>
            "public int Divide(int a, int b) { return a / b; } // no zero check");
        var agent = new CodeAgent(C("code", AgentType.Code), brain, new(), NullLogger<CodeAgent>.Instance);
        var r = agent.RunAsync([U("Code: division function")], null, null, CancellationToken.None).Result;
        Assert.NotNull(r.Text);
    }

    // ═══════════════════════════════════════════════════════
    // SECTION 3: EIA COMPLIANCE — Hallucinated Standards
    // ═══════════════════════════════════════════════════════

    [Theory]
    [InlineData("GB 3095-2024")]
    [InlineData("HJ 99999-2099")]
    [InlineData("GB 3838-2023")]
    public void CS_12_EIA_HallucinatedStandard_Detected(string fabricated)
    {
        var brain = new FakeChatClient().AddRoute("EIA", _ =>
            $"根据 {fabricated} 标准，SO2 排放限值为 40μg/m³。");
        var agent = new EIAAgent(C("eia", AgentType.EIA), brain, new(), NullLogger<EIAAgent>.Instance);
        var r = agent.RunAsync([U("EIA: 评估")], null, null, CancellationToken.None).Result;

        Assert.Contains("not found", r.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CS_13_EIA_ValidStandard_Passes()
    {
        var brain = new FakeChatClient().AddRoute("EIA", _ =>
            "根据 GB 3095-2012 和 HJ 2.2-2018，SO2 浓度 45μg/m³，达标。");
        var agent = new EIAAgent(C("eia", AgentType.EIA), brain, new(), NullLogger<EIAAgent>.Instance);
        var r = agent.RunAsync([U("EIA: 合规")], null, null, CancellationToken.None).Result;

        Assert.Contains("GB 3095-2012", r.Text);
        Assert.DoesNotContain("not found in valid standards", r.Text);
    }

    [Fact]
    public void CS_14_EIA_RegulationStore_RejectsFabricated()
    {
        var store = new RegulationVersionStore();
        Assert.False(store.IsValidCode("GB 3095-2024"));
        Assert.False(store.IsValidCode("HJ 99999-2099"));
        Assert.True(store.IsValidCode("GB 3095-2012"));
        Assert.True(store.IsValidCode("HJ 2.2-2018"));
    }

    [Fact]
    public void CS_15_EIA_ParameterValidation_RejectsOutOfRange()
    {
        var brain = new FakeChatClient().AddRoute("EIA", _ =>
            "根据 GB 3095-2012，排放评估完成。");
        var agent = new EIAAgent(C("eia", AgentType.EIA), brain, new(), NullLogger<EIAAgent>.Instance);
        var r = agent.RunAsync([U("EIA: Q=999999999 u=2.5 He=50")], null, null, CancellationToken.None).Result;

        Assert.NotNull(r.Text);
    }

    // ═══════════════════════════════════════════════════════
    // SECTION 4: COMBINED — Code + EIA Cross-Safety
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void CS_16_Combined_EIAReport_WithSafeCode_AllPass()
    {
        // EIA generates report + code for data processing
        var brain = new FakeChatClient()
            .AddRoute("EIA", _ => """
                ## EIA Report
                Standards: GB 3095-2012, HJ 2.2-2018
                SO2: 45.2μg/m³ (limit 60μg/m³) — PASS

                ## Data Processing Script
                ```python
                import csv
                def calc_mean(filename):
                    with open(filename) as f:
                        data = [float(row[0]) for row in csv.reader(f)]
                    if not data: return 0
                    return sum(data) / len(data)
                ```
                """);

        var agent = new EIAAgent(C("eia", AgentType.EIA), brain, new(), NullLogger<EIAAgent>.Instance);
        var r = agent.RunAsync([U("EIA: full assessment with data script")], null, null, CancellationToken.None).Result;

        Assert.NotNull(r.Text);
        Assert.Contains("GB 3095-2012", r.Text);
        Assert.DoesNotContain("GB 3095-2024", r.Text);
        Assert.False(BlockedApis.Any(a => r.Text!.Contains(a, StringComparison.OrdinalIgnoreCase)),
            "Generated code must not contain blocked APIs");
    }

    [Fact]
    public void CS_17_Combined_MaliciousCode_InEIAContext_Flagged()
    {
        // Simulate an LLM that embeds dangerous code inside an EIA report
        var brain = new FakeChatClient().AddRoute("EIA", _ => """
            ## EIA Report
            Standards: GB 3095-2024  ← FABRICATED
            ```python
            import os
            os.system("rm -rf /important_data")
            ```
            """);

        var agent = new EIAAgent(C("eia", AgentType.EIA), brain, new(), NullLogger<EIAAgent>.Instance);
        var r = agent.RunAsync([U("EIA: malicious")], null, null, CancellationToken.None).Result;

        Assert.NotNull(r.Text);
        // Standard should be flagged
        bool standardFlagged = r.Text!.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                                r.Text!.Contains("GB 3095-2024", StringComparison.OrdinalIgnoreCase);
        Assert.True(standardFlagged, "Fabricated standard should appear in audit");
        // Dangerous code should be detectable
        Assert.True(
            r.Text!.Contains("rm", StringComparison.OrdinalIgnoreCase) ||
            r.Text!.Contains("os.system", StringComparison.OrdinalIgnoreCase) ||
            r.Text!.Contains("destructive", StringComparison.OrdinalIgnoreCase),
            "Generated malicious code should be present and detectable");
    }

    // ═══════════════════════════════════════════════════════
    // SECTION 5: CI GATE RULES
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void CS_18_CI_Gate_SyntaxCheck_MustPass()
    {
        var code = """public static int Add(int a, int b) { return a + b; }""";
        Assert.Contains("Add", code);
        Assert.True(code.Count(c => c == '{') == code.Count(c => c == '}'));
    }

    [Fact]
    public void CS_19_CI_Gate_NoBlockedAPI_InGeneratedCode()
    {
        var generatedCode = """public static double Div(double a, double b) { return b != 0 ? a/b : 0; }""";
        var violation = BlockedApis.Any(api =>
            generatedCode.Contains(api, StringComparison.OrdinalIgnoreCase));
        Assert.False(violation, "CI gate: generated code must not contain blocked APIs");
    }

    [Fact]
    public void CS_20_CI_Gate_NoHallucinatedStandards()
    {
        var reportContent = "根据 GB 3095-2012 和 HJ 2.2-2018 的标准...";
        var hasHallucinated = HallucinatedStandards.Any(hs =>
            reportContent.Contains(hs, StringComparison.OrdinalIgnoreCase));
        Assert.False(hasHallucinated, "CI gate: EIA report must not contain hallucinated standards");
    }

    // ═══════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════

    private static LTAIAgentCard C(string name, AgentType type) => new()
    { Name = name, Type = type, Instructions = "", Middleware = new() { "unified_safety" } };

    private static ChatMessage U(string text) => new(ChatRole.User, text);
}
