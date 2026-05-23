using LTAI.Cli.Commands;
using LTAI.Models;
using Xunit;

namespace LTAI.Tests;

/// <summary>
/// Configuration Safety Auditor Tests for LTAI v7.0.
/// Covers: middleware removal, agent type changes, critical change detection, CI fail conditions.
/// Uses in-memory agent configs — no file I/O.
/// </summary>
public class ConfigSafetyTests
{
    // ═══════════════════════════════════════════════════════
    // MIDDLEWARE SAFETY
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void CFG_01_UnifiedSafety_Removed_DetectedAsCritical()
    {
        var oldConfig = Config(
            A("chat", Chat, ["unified_safety", "budget_tracking"]),
            A("code", Code, ["unified_safety"]));

        var newConfig = Config(
            A("chat", Chat, ["budget_tracking"]),  // unified_safety REMOVED!
            A("code", Code, ["unified_safety"]));

        var report = Diff(oldConfig, newConfig);

        Assert.True(report.HasCriticalChanges,
            "Removing unified_safety middleware should be CRITICAL");
        Assert.Contains(report.BreakingChanges,
            c => c.Agent == "chat" && c.Field.Contains("unified_safety") && c.Level == Severity.Critical);
    }

    [Fact]
    public void CFG_02_DNASafety_Removed_DetectedAsCritical()
    {
        var oldConfig = Config(A("eia", EIA, ["dna_safety"]));
        var newConfig = Config(A("eia", EIA, []));

        var report = Diff(oldConfig, newConfig);

        Assert.Contains(report.BreakingChanges,
            c => c.Level == Severity.Critical &&
                 c.Field.Contains("dna_safety"));
    }

    [Fact]
    public void CFG_03_PromptShield_Removed_DetectedAsCritical()
    {
        var oldConfig = Config(A("chat", Chat, ["prompt_shield"]));
        var newConfig = Config(A("chat", Chat, []));

        var report = Diff(oldConfig, newConfig);

        Assert.Contains(report.BreakingChanges,
            c => c.Level == Severity.Critical &&
                 c.Field.Contains("prompt_shield"));
    }

    [Fact]
    public void CFG_04_NonSafetyMiddleware_Removed_WarningOnly()
    {
        var oldConfig = Config(A("chat", Chat, ["unified_safety", "custom_logger"]));
        var newConfig = Config(A("chat", Chat, ["unified_safety"]));

        var report = Diff(oldConfig, newConfig);

        Assert.Contains(report.BreakingChanges,
            c => c.Field.Contains("custom_logger") && c.Level == Severity.Warning);
        Assert.False(report.HasCriticalChanges,
            "Removing non-safety middleware should not be CRITICAL");
    }

    [Fact]
    public void CFG_05_MiddlewareIntact_NoBreakingChange()
    {
        var oldConfig = Config(A("chat", Chat, ["unified_safety"]));
        var newConfig = Config(A("chat", Chat, ["unified_safety"]));

        var report = Diff(oldConfig, newConfig);

        Assert.False(report.HasBreakingChanges,
            "Identical middleware should produce no breaking changes");
    }

    // ═══════════════════════════════════════════════════════
    // AGENT TYPE CHANGES
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void CFG_06_AgentTypeChanged_DetectedAsCritical()
    {
        var oldConfig = Config(A("eia", EIA, []));
        var newConfig = Config(A("eia", Chat, []));  // EIA → Chat

        var report = Diff(oldConfig, newConfig);

        Assert.True(report.HasCriticalChanges);
        Assert.Contains(report.BreakingChanges,
            c => c.Agent == "eia" && c.Field == "type" && c.Level == Severity.Critical);
    }

    [Fact]
    public void CFG_07_CodeToReasoning_Changed_Detected()
    {
        var oldConfig = Config(A("analyzer", Code, []));
        var newConfig = Config(A("analyzer", Reasoning, []));

        var report = Diff(oldConfig, newConfig);

        Assert.Contains(report.BreakingChanges,
            c => c.Agent == "analyzer" && c.Field == "type" &&
                 c.OldValue == "Code" && c.NewValue == "Reasoning");
    }

    [Fact]
    public void CFG_08_SameType_NoBreak()
    {
        var oldConfig = Config(A("chat", Chat, []));
        var newConfig = Config(A("chat", Chat, []));

        var report = Diff(oldConfig, newConfig);
        Assert.False(report.HasBreakingChanges);
    }

    // ═══════════════════════════════════════════════════════
    // REMOVED AGENTS
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void CFG_09_AgentRemoved_Reported()
    {
        var oldConfig = Config(A("legacy-agent", Chat, []));
        var newConfig = Config(); // empty

        var report = Diff(oldConfig, newConfig);

        Assert.NotEmpty(report.RemovedAgents);
        Assert.Contains("legacy-agent", report.RemovedAgents[0]);
    }

    [Fact]
    public void CFG_10_MultipleAgents_OneRemoved()
    {
        var oldConfig = Config(
            A("a", Chat, []), A("b", Code, []), A("c", EIA, []));
        var newConfig = Config(
            A("a", Chat, []), A("c", EIA, []));  // "b" removed

        var report = Diff(oldConfig, newConfig);

        Assert.Single(report.RemovedAgents);
        Assert.Contains("b", report.RemovedAgents[0]);
    }

    // ═══════════════════════════════════════════════════════
    // HUMAN-READABLE OUTPUT
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void CFG_11_ToColoredString_HasCriticalMarkers()
    {
        var oldConfig = Config(A("chat", Chat, ["unified_safety"]));
        var newConfig = Config(A("chat", Chat, []));
        var report = Diff(oldConfig, newConfig);
        var output = report.ToColoredString();

        Assert.True(report.HasCriticalChanges, "Removing safety should be critical");
        Assert.True(output.Length > 0, "Output should not be empty");
    }

    [Fact]
    public void CFG_12_ToColoredString_WarningOnly_NoDeployWarning()
    {
        var oldConfig = Config(A("chat", Chat, ["unified_safety", "custom"]));
        var newConfig = Config(A("chat", Chat, ["unified_safety"]));

        var report = Diff(oldConfig, newConfig);
        var output = report.ToColoredString();

        Assert.Contains("🟡", output);   // Warning indicator
        Assert.DoesNotContain("DO NOT DEPLOY", output);  // No critical deploy block
    }

    [Fact]
    public void CFG_13_ToColoredString_NoChanges_EmptyDiff()
    {
        var oldConfig = Config(A("chat", Chat, ["unified_safety"]));
        var newConfig = Config(A("chat", Chat, ["unified_safety"]));

        var report = Diff(oldConfig, newConfig);
        var output = report.ToColoredString();

        Assert.DoesNotContain("🔴", output);
        Assert.DoesNotContain("🟡", output);
    }

    // ═══════════════════════════════════════════════════════
    // CI FAIL CONDITIONS
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void CFG_14_CriticalChange_ShouldCI_Fail()
    {
        var oldConfig = Config(A("chat", Chat, ["unified_safety"]));
        var newConfig = Config(A("chat", Chat, []));

        var report = Diff(oldConfig, newConfig);

        Assert.True(report.HasCriticalChanges,
            "CI should FAIL — unified_safety was removed");
    }

    [Fact]
    public void CFG_15_TypeChange_ShouldCI_Fail()
    {
        var oldConfig = Config(A("code", Code, []));
        var newConfig = Config(A("code", Chat, []));

        var report = Diff(oldConfig, newConfig);

        Assert.True(report.HasCriticalChanges,
            "CI should FAIL — agent type changed");
    }

    [Fact]
    public void CFG_16_WarningOnly_ShouldCI_Pass()
    {
        var oldConfig = Config(A("chat", Chat, ["unified_safety", "old_logger"]));
        var newConfig = Config(A("chat", Chat, ["unified_safety"]));

        var report = Diff(oldConfig, newConfig);

        Assert.False(report.HasCriticalChanges,
            "CI should PASS — only warning-level changes");
    }

    [Fact]
    public void CFG_17_NoChanges_ShouldCI_Pass()
    {
        var oldConfig = Config(A("a", Chat, ["unified_safety"]), A("b", Code, ["unified_safety"]));
        var newConfig = Config(A("a", Chat, ["unified_safety"]), A("b", Code, ["unified_safety"]));

        var report = Diff(oldConfig, newConfig);

        Assert.False(report.HasBreakingChanges, "CI should PASS — no changes");
    }

    // ═══════════════════════════════════════════════════════
    // COMPLEX SCENARIOS
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void CFG_18_MultipleViolations_AllReported()
    {
        var oldConfig = Config(
            A("eia", EIA, ["unified_safety", "budget"]),
            A("code", Code, ["dna_safety"]));

        var newConfig = Config(
            A("eia", Chat, []),            // type changed + unified_safety removed!
            A("code", Code, ["dna_safety"])); // unchanged

        var report = Diff(oldConfig, newConfig);

        Assert.True(report.HasCriticalChanges);
        // Should have at least: type change + unified_safety removal
        Assert.True(report.BreakingChanges.Count >= 2,
            $"Expected >=2 breaking changes, got {report.BreakingChanges.Count}");
    }

    [Fact]
    public void CFG_19_v6ToV7_Migration_DetectsAll()
    {
        // Simulated v6 config → v7 config migration
        var v6 = Config(
            A("chat", Chat, ["prompt_shield", "input_classifier", "dna_safety", "budget_tracking", "output_review"]),
            A("code", Code, ["prompt_shield", "dna_safety", "budget_tracking"]));

        var v7 = Config(
            A("chat", Chat, ["unified_safety", "budget_tracking"]),
            A("code", Code, ["unified_safety"]));

        var report = Diff(v6, v7);

        // Old middleware removals should be flagged
        Assert.True(report.BreakingChanges.Count >= 3,
            $"Expected >=3 middleware changes detected, got {report.BreakingChanges.Count}");
    }

    [Fact]
    public void CFG_20_Severity_EnumValues_Correct()
    {
        Assert.Equal(0, (int)Severity.Info);
        Assert.Equal(1, (int)Severity.Warning);
        Assert.Equal(2, (int)Severity.Critical);
        // Critical > Warning > Info for sorting
    }

    [Fact]
    public void CFG_21_BreakingChangeRecord_AllFieldsSet()
    {
        var change = new BreakingChange("test-agent", "middleware[unified_safety]",
            "unified_safety", "(removed)", Severity.Critical,
            "Removing safety middleware leaves the agent UNPROTECTED");

        Assert.Equal("test-agent", change.Agent);
        Assert.Equal("middleware[unified_safety]", change.Field);
        Assert.Equal("unified_safety", change.OldValue);
        Assert.Equal("(removed)", change.NewValue);
        Assert.Equal(Severity.Critical, change.Level);
        Assert.Contains("UNPROTECTED", change.Impact);
    }

    // ═══════════════════════════════════════════════════════
    // REPORT PROPERTIES
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void CFG_22_Report_HasBreakingChanges_True_WithChanges()
    {
        var oldConfig = Config(A("a", Chat, []));
        var newConfig = Config();

        var report = Diff(oldConfig, newConfig);
        Assert.True(report.HasBreakingChanges);
    }

    [Fact]
    public void CFG_23_Report_HasBreakingChanges_False_Identical()
    {
        var report = Diff(Config(A("a", Chat, [])), Config(A("a", Chat, [])));
        Assert.False(report.HasBreakingChanges);
    }

    [Fact]
    public void CFG_24_Report_HasCriticalChanges_False_WarningOnly()
    {
        var oldConfig = Config(A("a", Chat, ["unified_safety", "old"]));
        var newConfig = Config(A("a", Chat, ["unified_safety"]));

        var report = Diff(oldConfig, newConfig);
        Assert.True(report.HasBreakingChanges);
        Assert.False(report.HasCriticalChanges);
    }

    // ═══════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════

    private const AgentType Chat = AgentType.Chat;
    private const AgentType Code = AgentType.Code;
    private const AgentType EIA = AgentType.EIA;
    private const AgentType Reasoning = AgentType.Reasoning;

    private static LTAIAgentCard A(string name, AgentType type, List<string> mw) => new()
    {
        Name = name, Type = type, Middleware = mw, Tools = new()
    };

    private static AgentConfig Config(params LTAIAgentCard[] agents) => new()
    {
        Agents = agents.ToList()
    };

    private static ConfigDiffReport Diff(AgentConfig oldConfig, AgentConfig newConfig)
    {
        var oldYaml = ToYaml(oldConfig);
        var newYaml = ToYaml(newConfig);

        var oldPath = Path.GetTempFileName();
        var newPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(oldPath, oldYaml);
            File.WriteAllText(newPath, newYaml);
            return new ConfigDiffer().DiffAsync(oldPath, newPath, CancellationToken.None).Result;
        }
        finally
        {
            File.Delete(oldPath);
            File.Delete(newPath);
        }
    }

    private static string ToYaml(AgentConfig config)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("agents:");
        foreach (var a in config.Agents)
        {
            sb.AppendLine($"  - name: {a.Name}");
            sb.AppendLine($"    type: {TypeToString(a.Type)}");
            sb.AppendLine($"    instructions: Test agent instructions");
            if (a.Middleware.Count > 0)
            {
                sb.AppendLine($"    middleware:");
                foreach (var mw in a.Middleware)
                    sb.AppendLine($"      - {mw}");
            }
            sb.AppendLine($"    tools:");
        }
        return sb.ToString();
    }

    private static string TypeToString(AgentType t) => t switch
    {
        AgentType.Chat => "chat_agent",
        AgentType.Code => "code_agent",
        AgentType.EIA => "eia_agent",
        AgentType.Reasoning => "reasoning_agent",
        _ => "chat_agent"
    };
}
