using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LTAI.CLI.Improve;

/// <summary>
/// 改革方案
/// </summary>
public sealed record ReformProposal
{
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public int Priority { get; init; } // 1-5, 5 最高
    public string BasedOnPaper { get; init; } = "";
    public string TargetModule { get; init; } = "";
    public string ImplementationPath { get; init; } = "";
    public string ExpectedBenefit { get; init; } = "";
    public string Risks { get; init; } = "";
    public EstimatedEffort Effort { get; init; }
}

/// <summary>
/// 预估工作量
/// </summary>
public enum EstimatedEffort
{
    QuickWin,       // < 1 天
    ShortTerm,      // 1-3 天
    MediumTerm,     // 1-2 周
    LongTerm        // > 2 周
}

/// <summary>
/// 完整改革提案
/// </summary>
public sealed record ReformProposalDocument
{
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;
    public ArchitectureAuditReport AuditReport { get; init; } = new();
    public List<PaperInsight> RelevantPapers { get; init; } = new();
    public List<InnovationMatch> Matches { get; init; } = new();
    public List<ReformProposal> Proposals { get; init; } = new();
}

/// <summary>
/// 改革方案生成器
/// 输出结构化改革方案 (含优先级、实现路径、预期收益)
/// </summary>
public sealed class ReformProposalGenerator
{
    /// <summary>
    /// 生成完整改革提案
    /// </summary>
    public ReformProposalDocument Generate(
        ArchitectureAuditReport auditReport,
        List<PaperInsight> papers,
        List<InnovationMatch> matches)
    {
        var proposals = GenerateProposals(auditReport, papers, matches);

        return new ReformProposalDocument
        {
            GeneratedAt = DateTime.UtcNow,
            AuditReport = auditReport,
            RelevantPapers = papers,
            Matches = matches,
            Proposals = proposals.OrderByDescending(p => p.Priority).ToList()
        };
    }

    private static List<ReformProposal> GenerateProposals(
        ArchitectureAuditReport audit,
        List<PaperInsight> papers,
        List<InnovationMatch> matches)
    {
        var proposals = new List<ReformProposal>();

        // 1. 基于架构问题的提案
        foreach (var issue in audit.Issues.Where(i => i.Severity >= 4))
        {
            proposals.Add(new ReformProposal
            {
                Title = $"Fix {issue.Type}: {issue.Component}",
                Description = issue.Description,
                Priority = issue.Severity,
                BasedOnPaper = "Architecture Audit",
                TargetModule = issue.Component,
                ImplementationPath = issue.SuggestedFix,
                ExpectedBenefit = issue.Impact.Replace("Limited", "Enable").Replace("Difficult", "Easy"),
                Risks = "May require refactoring existing code",
                Effort = issue.Type == ArchitectureIssueType.MissingModule ? EstimatedEffort.MediumTerm : EstimatedEffort.ShortTerm
            });
        }

        // 2. 基于论文匹配的提案
        var topMatches = matches.Take(5).ToList();
        foreach (var match in topMatches)
        {
            proposals.Add(new ReformProposal
            {
                Title = $"Integrate {match.Paper.Title.Split('(')[0].Trim()} into {match.LTAIModule}",
                Description = match.MatchReason,
                Priority = (int)Math.Round(match.MatchScore * 5),
                BasedOnPaper = $"{match.Paper.Title} ({match.Paper.ArxivId})",
                TargetModule = match.LTAIModule,
                ImplementationPath = match.SuggestedIntegration,
                ExpectedBenefit = match.ExpectedBenefit,
                Risks = "Integration complexity, potential regression",
                Effort = match.MatchScore > 0.9 ? EstimatedEffort.MediumTerm : EstimatedEffort.ShortTerm
            });
        }

        // 3. 前瞻性创新提案 (基于趋势分析)
        proposals.Add(new ReformProposal
        {
            Title = "Implement Multi-Modal CodeGraph Analysis",
            Description = "Extend CodeGraph to support multi-modal analysis (code + docs + tests)",
            Priority = 3,
            BasedOnPaper = "Industry Trend",
            TargetModule = "CodeGraphEnhanced",
            ImplementationPath = "Add document and test parsers, build cross-modal dependency graph",
            ExpectedBenefit = "Better understanding of codebase context, improved code review capabilities",
            Risks = "Increased indexing time and storage",
            Effort = EstimatedEffort.LongTerm
        });

        proposals.Add(new ReformProposal
        {
            Title = "Add Observability Dashboard",
            Description = "Real-time monitoring of L1/L2 routing decisions, PACE metrics, SePT collection stats",
            Priority = 4,
            BasedOnPaper = "Architecture Audit (Missing Module)",
            TargetModule = "System",
            ImplementationPath = "Integrate OpenTelemetry, build web dashboard with key metrics",
            ExpectedBenefit = "Better visibility into system behavior, faster debugging and optimization",
            Risks = "Performance overhead from telemetry collection",
            Effort = EstimatedEffort.MediumTerm
        });

        return proposals;
    }

    /// <summary>
    /// 生成提案文档文本
    /// </summary>
    public static string GenerateDocumentText(ReformProposalDocument doc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== LTAI Reform Proposal Document ===\n");
        sb.AppendLine($"Generated: {doc.GeneratedAt:yyyy-MM-dd HH:mm:ss}\n");

        // 架构审计摘要
        sb.AppendLine("--- Architecture Audit Summary ---");
        sb.AppendLine($"Components: {doc.AuditReport.TotalComponents}");
        sb.AppendLine($"Critical Issues: {doc.AuditReport.CriticalIssues}");
        sb.AppendLine($"High Issues: {doc.AuditReport.HighIssues}\n");

        // 相关论文
        sb.AppendLine("--- Relevant Papers (Last 30 Days) ---");
        foreach (var paper in doc.RelevantPapers.Take(5))
        {
            sb.AppendLine($"  [{paper.ArxivId}] {paper.Title}");
            sb.AppendLine($"    Authors: {paper.Authors}");
            sb.AppendLine($"    Relevance: {paper.RelevanceScore:P0}");
            sb.AppendLine($"    Innovations: {string.Join(", ", paper.KeyInnovations.Take(2))}");
            sb.AppendLine();
        }

        // 改革提案
        sb.AppendLine("--- Reform Proposals (by Priority) ---");
        foreach (var proposal in doc.Proposals)
        {
            var priorityLabel = proposal.Priority switch { 5 => "P0-CRITICAL", 4 => "P1-HIGH", 3 => "P2-MEDIUM", _ => "P3-LOW" };
            sb.AppendLine($"  [{priorityLabel}] {proposal.Title}");
            sb.AppendLine($"    Based on: {proposal.BasedOnPaper}");
            sb.AppendLine($"    Target: {proposal.TargetModule}");
            sb.AppendLine($"    Description: {proposal.Description}");
            sb.AppendLine($"    Implementation: {proposal.ImplementationPath}");
            sb.AppendLine($"    Expected Benefit: {proposal.ExpectedBenefit}");
            sb.AppendLine($"    Risks: {proposal.Risks}");
            sb.AppendLine($"    Effort: {proposal.Effort}");
            sb.AppendLine();
        }

        // 执行建议
        sb.AppendLine("--- Recommended Execution Order ---");
        var quickWins = doc.Proposals.Where(p => p.Effort == EstimatedEffort.QuickWin).ToList();
        var shortTerm = doc.Proposals.Where(p => p.Effort == EstimatedEffort.ShortTerm).ToList();
        var mediumTerm = doc.Proposals.Where(p => p.Effort == EstimatedEffort.MediumTerm).ToList();
        var longTerm = doc.Proposals.Where(p => p.Effort == EstimatedEffort.LongTerm).ToList();

        if (quickWins.Any()) sb.AppendLine($"  Quick Wins ({quickWins.Count}): {string.Join(", ", quickWins.Select(p => p.Title))}");
        if (shortTerm.Any()) sb.AppendLine($"  Short Term ({shortTerm.Count}): {string.Join(", ", shortTerm.Select(p => p.Title))}");
        if (mediumTerm.Any()) sb.AppendLine($"  Medium Term ({mediumTerm.Count}): {string.Join(", ", mediumTerm.Select(p => p.Title))}");
        if (longTerm.Any()) sb.AppendLine($"  Long Term ({longTerm.Count}): {string.Join(", ", longTerm.Select(p => p.Title))}");

        return sb.ToString();
    }
}
