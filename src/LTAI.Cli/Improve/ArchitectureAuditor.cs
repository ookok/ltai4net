using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LTAI.CLI.Improve;

/// <summary>
/// 架构问题类型
/// </summary>
public enum ArchitectureIssueType
{
    MissingModule,      // 缺少关键模块
    LongChain,          // 链路过长
    UnclosedLoop,       // 未形成闭环
    PerformanceBottleneck, // 性能瓶颈
    Redundancy,         // 冗余设计
    TightCoupling       // 紧耦合
}

/// <summary>
/// 架构问题描述
/// </summary>
public sealed record ArchitectureIssue
{
    public ArchitectureIssueType Type { get; init; }
    public string Component { get; init; } = "";
    public string Description { get; init; } = "";
    public string Impact { get; init; } = "";
    public int Severity { get; init; } // 1-5, 5 最严重
    public string SuggestedFix { get; init; } = "";
}

/// <summary>
/// 架构组件描述
/// </summary>
public sealed record ArchitectureComponent
{
    public string Name { get; init; } = "";
    public string Responsibility { get; init; } = "";
    public List<string> Dependencies { get; init; } = new();
    public List<string> Inputs { get; init; } = new();
    public List<string> Outputs { get; init; } = new();
    public bool HasFeedbackLoop { get; init; }
}

/// <summary>
/// 架构审计报告
/// </summary>
public sealed record ArchitectureAuditReport
{
    public DateTime AuditTime { get; init; } = DateTime.UtcNow;
    public List<ArchitectureComponent> Components { get; init; } = new();
    public List<ArchitectureIssue> Issues { get; init; } = new();
    public int TotalComponents { get; init; }
    public int CriticalIssues { get; init; }
    public int HighIssues { get; init; }
    public int MediumIssues { get; init; }
    public int LowIssues { get; init; }
}

/// <summary>
/// 架构审计器
/// 分析当前架构拓扑，识别瓶颈和改进点
/// </summary>
public sealed class ArchitectureAuditor
{
    /// <summary>
    /// 执行架构审计
    /// </summary>
    public ArchitectureAuditReport Audit()
    {
        var components = DefineCurrentArchitecture();
        var issues = IdentifyIssues(components);

        return new ArchitectureAuditReport
        {
            Components = components,
            Issues = issues,
            TotalComponents = components.Count,
            CriticalIssues = issues.Count(i => i.Severity >= 5),
            HighIssues = issues.Count(i => i.Severity == 4),
            MediumIssues = issues.Count(i => i.Severity == 3),
            LowIssues = issues.Count(i => i.Severity <= 2)
        };
    }

    private static List<ArchitectureComponent> DefineCurrentArchitecture()
    {
        return new List<ArchitectureComponent>
        {
            new() { Name = "L1L2DuplexRouter", Responsibility = "路由决策", Dependencies = new() { "L1Engine", "L2Client", "Cache", "RecursivePipeline" }, Inputs = new() { "Query" }, Outputs = new() { "RouteResult" }, HasFeedbackLoop = true },
            new() { Name = "L1Engine (GGUF/ONNX)", Responsibility = "本地推理", Dependencies = new() { "Tokenizer" }, Inputs = new() { "Prompt" }, Outputs = new() { "Response", "LatentState" }, HasFeedbackLoop = false },
            new() { Name = "RecursiveLatentPipeline", Responsibility = "潜空间递归协作", Dependencies = new() { "L1Engine", "L2Engine", "RecursiveLink" }, Inputs = new() { "Prompt" }, Outputs = new() { "Response" }, HasFeedbackLoop = true },
            new() { Name = "SelectiveThinkingPipeline", Responsibility = "Token 级 TaH 推理", Dependencies = new() { "L1Engine", "L2Client", "TokenHardnessDecider" }, Inputs = new() { "Prompt" }, Outputs = new() { "TokenStream" }, HasFeedbackLoop = true },
            new() { Name = "LearningProgressTracker", Responsibility = "PACE 学习进度监控", Dependencies = new(), Inputs = new() { "ParameterChanges" }, Outputs = new() { "Metrics" }, HasFeedbackLoop = false },
            new() { Name = "FailureAttributionEngine", Responsibility = "LIFE 故障归因", Dependencies = new() { "LearningProgressTracker" }, Inputs = new() { "TaskTrace" }, Outputs = new() { "AttributionReport" }, HasFeedbackLoop = false },
            new() { Name = "SelfEvolutionLoop", Responsibility = "LIFE 结构演化", Dependencies = new() { "FailureAttributionEngine" }, Inputs = new() { "AttributionReport" }, Outputs = new() { "EvolutionActions" }, HasFeedbackLoop = true },
            new() { Name = "SePTMemoryBank", Responsibility = "SePT 经验库", Dependencies = new() { "SePTDataCollector" }, Inputs = new() { "HighQualitySamples" }, Outputs = new() { "FewShotExamples" }, HasFeedbackLoop = false },
            new() { Name = "UnifiedRewardModel", Responsibility = "多维度奖励评估", Dependencies = new() { "TraceEfficiencyReward", "InverseRewardModel" }, Inputs = new() { "Query", "Response" }, Outputs = new() { "RewardSignal" }, HasFeedbackLoop = true },
            new() { Name = "CodeGraphEnhanced", Responsibility = "代码图谱分析", Dependencies = new() { "LanguageParsers" }, Inputs = new() { "SourceCode" }, Outputs = new() { "CallGraph", "Fingerprints" }, HasFeedbackLoop = false },
            new() { Name = "StealthBrowser", Responsibility = "隐身浏览能力", Dependencies = new() { "Playwright" }, Inputs = new() { "URL" }, Outputs = new() { "PageContent" }, HasFeedbackLoop = false }
        };
    }

    private static List<ArchitectureIssue> IdentifyIssues(List<ArchitectureComponent> components)
    {
        var issues = new List<ArchitectureIssue>();

        // 1. 检查缺失的关键模块
        var expectedModules = new[] { "HumanFeedbackCollector", "A/BTestFramework", "ObservabilityDashboard" };
        foreach (var module in expectedModules)
        {
            if (!components.Any(c => c.Name.Contains(module, StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add(new ArchitectureIssue
                {
                    Type = ArchitectureIssueType.MissingModule,
                    Component = "System",
                    Description = $"Missing critical module: {module}",
                    Impact = "Limited ability to collect rewards, gather human feedback, run experiments, or monitor system health",
                    Severity = 4,
                    SuggestedFix = $"Implement {module} to close the loop on self-improvement"
                });
            }
        }

        // 2. 检查未形成闭环的组件
        var componentsWithoutFeedback = components.Where(c => !c.HasFeedbackLoop && c.Outputs.Any()).ToList();
        foreach (var comp in componentsWithoutFeedback.Take(3))
        {
            issues.Add(new ArchitectureIssue
            {
                Type = ArchitectureIssueType.UnclosedLoop,
                Component = comp.Name,
                Description = $"Component '{comp.Name}' produces outputs but has no feedback loop to learn from them",
                Impact = "System cannot self-improve based on the outputs of this component",
                Severity = 3,
                SuggestedFix = $"Add a feedback mechanism from {comp.Name} outputs back to its inputs or configuration"
            });
        }

        // 3. 检查潜在的性能瓶颈
        issues.Add(new ArchitectureIssue
        {
            Type = ArchitectureIssueType.PerformanceBottleneck,
            Component = "RecursiveLatentPipeline",
            Description = "Recursive latent space transfers involve multiple model forward passes and matrix multiplications",
            Impact = "High latency for complex queries (potentially 2-4x slower than single-pass generation)",
            Severity = 3,
            SuggestedFix = "Implement early stopping (already done via PACE), consider caching intermediate latent states"
        });

        // 4. 检查紧耦合问题
        issues.Add(new ArchitectureIssue
        {
            Type = ArchitectureIssueType.TightCoupling,
            Component = "L1L2DuplexRouter",
            Description = "Router directly instantiates and depends on multiple concrete components (RecursivePipeline, AttributionEngine, EvolutionLoop)",
            Impact = "Difficult to test, replace, or scale individual components independently",
            Severity = 2,
            SuggestedFix = "Introduce interfaces and dependency injection for better decoupling"
        });

        return issues.OrderByDescending(i => i.Severity).ToList();
    }

    /// <summary>
    /// 生成审计报告文本
    /// </summary>
    public static string GenerateReportText(ArchitectureAuditReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== LTAI Architecture Audit Report ===\n");
        sb.AppendLine($"Audit Time: {report.AuditTime:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Total Components: {report.TotalComponents}");
        sb.AppendLine($"Critical Issues (5): {report.CriticalIssues}");
        sb.AppendLine($"High Issues (4): {report.HighIssues}");
        sb.AppendLine($"Medium Issues (3): {report.MediumIssues}");
        sb.AppendLine($"Low Issues (1-2): {report.LowIssues}\n");

        sb.AppendLine("--- Component Map ---");
        foreach (var comp in report.Components)
        {
            sb.AppendLine($"  [{comp.Name}]");
            sb.AppendLine($"    Responsibility: {comp.Responsibility}");
            sb.AppendLine($"    Dependencies: {string.Join(", ", comp.Dependencies)}");
            sb.AppendLine($"    Feedback Loop: {(comp.HasFeedbackLoop ? "Yes" : "No")}");
        }

        sb.AppendLine("\n--- Issues by Severity ---");
        foreach (var issue in report.Issues)
        {
            var severityLabel = issue.Severity switch { 5 => "CRITICAL", 4 => "HIGH", 3 => "MEDIUM", _ => "LOW" };
            sb.AppendLine($"  [{severityLabel}] [{issue.Type}] {issue.Component}");
            sb.AppendLine($"    {issue.Description}");
            sb.AppendLine($"    Impact: {issue.Impact}");
            sb.AppendLine($"    Fix: {issue.SuggestedFix}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
