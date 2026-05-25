using System;
using System.IO;
using System.Text;
using LTAI.Cli.Improve;

namespace LTAI.Cli;

internal static class ImproveMode
{
    private static string DocsDir
    {
        get
        {
            var baseDir = AppContext.BaseDirectory;
            var rootDir = FindRootDirectory(baseDir, "docs");
            if (rootDir != null)
                return Path.Combine(rootDir, "docs", "improve");
            return Path.Combine(baseDir, "docs", "improve");
        }
    }

    public static async Task RunAsync(bool scan, bool papers, bool propose, bool auto)
    {
        Console.WriteLine("=== LTAI Improve Mode ===\n");

        var docDir = Path.GetFullPath(DocsDir);
        Directory.CreateDirectory(docDir);

        ArchitectureAuditReport? auditReport = null;
        List<PaperInsight>? paperList = null;

        if (auto || scan)
        {
            Console.WriteLine("🔍 Scanning architecture...\n");
            var auditor = new ArchitectureAuditor();
            auditReport = auditor.Audit();
            Console.WriteLine(ArchitectureAuditor.GenerateReportText(auditReport));
        }

        if (auto || papers)
        {
            Console.WriteLine("\n📚 Searching recent AI papers using WebSearchTools...\n");
            var searchAgent = new PaperSearchAgent();
            paperList = await searchAgent.SearchRecentPapersAsync().ConfigureAwait(false);
            
            Console.WriteLine($"Found {paperList.Count} relevant papers:");
            foreach (var p in paperList.Take(5))
            {
                Console.WriteLine($"  [{p.ArxivId}] {p.Title}");
                Console.WriteLine($"    Authors: {p.Authors} | Relevance: {p.RelevanceScore:P0}");
                Console.WriteLine($"    Innovations: {string.Join(", ", p.KeyInnovations.Take(2))}");
                Console.WriteLine();
            }
        }

        ReformProposalDocument? proposalDoc = null;
        if (auto || propose)
        {
            if (auditReport == null)
            {
                var auditor = new ArchitectureAuditor();
                auditReport = auditor.Audit();
            }
            if (paperList == null)
            {
                var searchAgent = new PaperSearchAgent();
                paperList = await searchAgent.SearchRecentPapersAsync().ConfigureAwait(false);
            }

            Console.WriteLine("\n🔗 Matching innovations with architecture...\n");
            var matcher = new InnovationMatcher(auditReport);
            var matches = matcher.Match(paperList);
            Console.WriteLine($"Found {matches.Count} potential matches.");

            Console.WriteLine("\n📝 Generating reform proposals...\n");
            var proposalGen = new ReformProposalGenerator();
            proposalDoc = proposalGen.Generate(auditReport, paperList, matches);
            Console.WriteLine(ReformProposalGenerator.GenerateDocumentText(proposalDoc));
        }

        var ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        if (auditReport != null)
        {
            var auditPath = Path.Combine(docDir, $"audit_{ts}.md");
            await File.WriteAllTextAsync(auditPath, GenerateAuditDocument(auditReport), default).ConfigureAwait(false);
            Console.WriteLine($"\n📄 Audit report saved: {auditPath}");
        }

        if (paperList != null)
        {
            var papersPath = Path.Combine(docDir, $"papers_{ts}.md");
            await File.WriteAllTextAsync(papersPath, GeneratePapersDocument(paperList), default).ConfigureAwait(false);
            Console.WriteLine($"\n📄 Papers report saved: {papersPath}");
        }

        if (proposalDoc != null)
        {
            var proposalPath = Path.Combine(docDir, $"proposal_{ts}.md");
            await File.WriteAllTextAsync(proposalPath, GenerateProposalDocument(proposalDoc), default).ConfigureAwait(false);
            Console.WriteLine($"\n📄 Proposal document saved: {proposalPath}");
        }

        Console.WriteLine("\n✅ Improve mode completed.");
    }

    private static string GenerateAuditDocument(ArchitectureAuditReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# LTAI Architecture Audit Report");
        sb.AppendLine();
        sb.AppendLine($"**Audit Time:** {report.AuditTime:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|--------|-------|");
        sb.AppendLine($"| Total Components | {report.TotalComponents} |");
        sb.AppendLine($"| Critical Issues | {report.CriticalIssues} |");
        sb.AppendLine($"| High Issues | {report.HighIssues} |");
        sb.AppendLine($"| Medium Issues | {report.MediumIssues} |");
        sb.AppendLine($"| Low Issues | {report.LowIssues} |");
        sb.AppendLine();

        sb.AppendLine("## Component Map");
        sb.AppendLine();
        sb.AppendLine("| Component | Responsibility | Dependencies | Feedback Loop |");
        sb.AppendLine("|-----------|----------------|--------------|---------------|");
        foreach (var comp in report.Components)
        {
            var deps = string.Join(", ", comp.Dependencies);
            sb.AppendLine($"| {comp.Name} | {comp.Responsibility} | {deps} | {(comp.HasFeedbackLoop ? "✅ Yes" : "No")} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Issues");
        sb.AppendLine();
        foreach (var issue in report.Issues)
        {
            var severity = issue.Severity switch { 5 => "🔴 CRITICAL", 4 => "🟠 HIGH", 3 => "🟡 MEDIUM", _ => "🟢 LOW" };
            sb.AppendLine($"### {severity} [{issue.Type}] {issue.Component}");
            sb.AppendLine();
            sb.AppendLine($"- **Description:** {issue.Description}");
            sb.AppendLine($"- **Impact:** {issue.Impact}");
            sb.AppendLine($"- **Suggested Fix:** {issue.SuggestedFix}");
            sb.AppendLine();
        }

        sb.AppendLine("---\n*Generated by LTAI Improve Mode*");
        return sb.ToString();
    }

    private static string GeneratePapersDocument(List<PaperInsight> papers)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# LTAI Recent AI Papers Survey");
        sb.AppendLine();
        sb.AppendLine($"**Generated:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"**Total Papers:** {papers.Count}");
        sb.AppendLine();

        sb.AppendLine("## Papers");
        sb.AppendLine();
        foreach (var p in papers)
        {
            sb.AppendLine($"### [{p.ArxivId}] {p.Title}");
            sb.AppendLine();
            sb.AppendLine($"- **Authors:** {p.Authors}");
            sb.AppendLine($"- **Relevance:** {p.RelevanceScore:P0}");
            sb.AppendLine($"- **Key Innovations:**");
            foreach (var innovation in p.KeyInnovations)
                sb.AppendLine($"  - {innovation}");
            if (!string.IsNullOrEmpty(p.Abstract))
            {
                sb.AppendLine();
                sb.AppendLine($"> {Truncate(p.Abstract, 200)}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("---\n*Generated by LTAI PaperSearchAgent*");
        return sb.ToString();
    }

    private static string GenerateProposalDocument(ReformProposalDocument doc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# LTAI Reform Proposals");
        sb.AppendLine();
        sb.AppendLine($"**Generated:** {doc.GeneratedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        sb.AppendLine("## Architecture Audit Summary");
        sb.AppendLine();
        sb.AppendLine($"- **Components:** {doc.AuditReport.TotalComponents}");
        sb.AppendLine($"- **Critical Issues:** {doc.AuditReport.CriticalIssues}");
        sb.AppendLine($"- **High Issues:** {doc.AuditReport.HighIssues}");
        sb.AppendLine();

        if (doc.RelevantPapers.Count > 0)
        {
            sb.AppendLine("## Relevant Papers");
            sb.AppendLine();
            foreach (var p in doc.RelevantPapers)
            {
                sb.AppendLine($"- [{p.ArxivId}] {p.Title} (Relevance: {p.RelevanceScore:P0})");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Reform Proposals");
        sb.AppendLine();
        foreach (var prop in doc.Proposals.OrderByDescending(p => p.Priority))
        {
            var priorityLabel = prop.Priority switch
            {
                5 => "🔴 P0-CRITICAL",
                4 => "🟠 P1-HIGH",
                3 => "🟡 P2-MEDIUM",
                _ => "🟢 P3-LOW"
            };
            sb.AppendLine($"### {priorityLabel} {prop.Title}");
            sb.AppendLine();
            sb.AppendLine($"- **Based on:** {prop.BasedOnPaper}");
            sb.AppendLine($"- **Target:** {prop.TargetModule}");
            sb.AppendLine($"- **Description:** {prop.Description}");
            sb.AppendLine($"- **Implementation:** {prop.ImplementationPath}");
            sb.AppendLine($"- **Expected Benefit:** {prop.ExpectedBenefit}");
            sb.AppendLine($"- **Risks:** {prop.Risks}");
            sb.AppendLine($"- **Effort:** {prop.Effort}");
            sb.AppendLine();
        }

        var quickWins = doc.Proposals.Where(p => p.Effort == EstimatedEffort.QuickWin).ToList();
        var shortTerm = doc.Proposals.Where(p => p.Effort == EstimatedEffort.ShortTerm).ToList();
        var mediumTerm = doc.Proposals.Where(p => p.Effort == EstimatedEffort.MediumTerm).ToList();
        var longTerm = doc.Proposals.Where(p => p.Effort == EstimatedEffort.LongTerm).ToList();

        if (quickWins.Any() || shortTerm.Any() || mediumTerm.Any() || longTerm.Any())
        {
            sb.AppendLine("## Recommended Execution Order");
            sb.AppendLine();
            if (quickWins.Any()) { sb.AppendLine($"### Quick Wins ({quickWins.Count})"); sb.AppendLine(); foreach (var p in quickWins) sb.AppendLine($"- {p.Title}"); sb.AppendLine(); }
            if (shortTerm.Any()) { sb.AppendLine($"### Short Term ({shortTerm.Count})"); sb.AppendLine(); foreach (var p in shortTerm) sb.AppendLine($"- {p.Title}"); sb.AppendLine(); }
            if (mediumTerm.Any()) { sb.AppendLine($"### Medium Term ({mediumTerm.Count})"); sb.AppendLine(); foreach (var p in mediumTerm) sb.AppendLine($"- {p.Title}"); sb.AppendLine(); }
            if (longTerm.Any()) { sb.AppendLine($"### Long Term ({longTerm.Count})"); sb.AppendLine(); foreach (var p in longTerm) sb.AppendLine($"- {p.Title}"); sb.AppendLine(); }
        }

        sb.AppendLine("---\n*Generated by LTAI Improve Mode*");
        return sb.ToString();
    }

    private static string Truncate(string? text, int maxLen) => CliUtilities.Truncate(text, maxLen);
    private static string? FindRootDirectory(string startDir, string markerDir) => CliUtilities.FindRootDirectory(startDir, markerDir);
}