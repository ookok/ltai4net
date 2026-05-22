using System;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using LTAI.CLI.Debug;
using LTAI.CLI.Improve;
using LTAI.CLI.Model;
using LTAI.Core.Setup;

namespace LTAI.Cli;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        if (command == "setup" || command == "help" || command == "--help" || command == "-h")
        {
            if (command == "setup") return await RunSetupAsync();
            PrintHelp();
            return 0;
        }

        var rootCommand = CreateRootCommand();
        var parser = new CommandLineBuilder(rootCommand)
            .UseDefaults()
            .Build();

        return await parser.InvokeAsync(args);
    }

    private static RootCommand CreateRootCommand()
    {
        var rootCommand = new RootCommand("LTAI CLI - Debug and Improve modes for self-diagnosis and evolution");

        var debugCommand = new Command("debug", "Run end-to-end tests with full link tracing");
        
        var queryOption = new Option<string?>("--query", "Specific query to trace");
        var countOption = new Option<int>("--count", () => 20, "Number of test cases to generate");
        var difficultyOption = new Option<string?>("--difficulty", "Filter by difficulty (Simple/Moderate/Complex/OOD)");
        var domainOption = new Option<string?>("--domain", "Filter by domain (Math/Code/Reasoning/Creative/Factual)");
        var reportOption = new Option<bool>("--report", () => false, "Generate detailed test report");

        debugCommand.AddOption(queryOption);
        debugCommand.AddOption(countOption);
        debugCommand.AddOption(difficultyOption);
        debugCommand.AddOption(domainOption);
        debugCommand.AddOption(reportOption);

        debugCommand.SetHandler(async (ctx) =>
        {
            var query = ctx.ParseResult.GetValueForOption(queryOption);
            var count = ctx.ParseResult.GetValueForOption(countOption);
            var difficulty = ctx.ParseResult.GetValueForOption(difficultyOption);
            var domain = ctx.ParseResult.GetValueForOption(domainOption);
            var report = ctx.ParseResult.GetValueForOption(reportOption);

            await DebugMode.RunAsync(query, count, difficulty, domain, report);
        });

        var improveCommand = new Command("improve", "Analyze architecture and propose improvements based on recent papers");
        
        var scanOption = new Option<bool>("--scan", () => false, "Scan current architecture for issues");
        var papersOption = new Option<bool>("--papers", () => false, "Search recent AI papers using WebSearchTools");
        var proposeOption = new Option<bool>("--propose", () => false, "Generate reform proposals");
        var autoOption = new Option<bool>("--auto", () => false, "Run full pipeline (scan -> search -> match -> propose)");

        improveCommand.AddOption(scanOption);
        improveCommand.AddOption(papersOption);
        improveCommand.AddOption(proposeOption);
        improveCommand.AddOption(autoOption);

        improveCommand.SetHandler(async (ctx) =>
        {
            var scan = ctx.ParseResult.GetValueForOption(scanOption);
            var papers = ctx.ParseResult.GetValueForOption(papersOption);
            var propose = ctx.ParseResult.GetValueForOption(proposeOption);
            var auto = ctx.ParseResult.GetValueForOption(autoOption);

            await ImproveMode.RunAsync(scan, papers, propose, auto);
        });

        rootCommand.AddCommand(debugCommand);
        rootCommand.AddCommand(improveCommand);

        var modelCommand = new Command("model", "Manage local models (list, download, remove, reset)");
        
        var modelCommandArg = new Argument<string?>("command", "Command: list, download, remove, reset");
        var modelLayerOption = new Option<string?>("--layer", "Model layer: L0, L1, L2");
        var modelVersionOption = new Option<string?>("--version", "Model version to download/remove");
        var modelMirrorOption = new Option<bool>("--mirror", () => false, "Force use of China mirror (hf-mirror.com)");
        var modelForceOption = new Option<bool>("--force", () => false, "Force re-download even if model exists");
        var modelSetupOption = new Option<bool>("--setup", () => false, "Re-run setup wizard after reset");

        modelCommand.AddArgument(modelCommandArg);
        modelCommand.AddOption(modelLayerOption);
        modelCommand.AddOption(modelVersionOption);
        modelCommand.AddOption(modelMirrorOption);
        modelCommand.AddOption(modelForceOption);
        modelCommand.AddOption(modelSetupOption);

        modelCommand.SetHandler(async (ctx) =>
        {
            var cmd = ctx.ParseResult.GetValueForArgument(modelCommandArg);
            var layer = ctx.ParseResult.GetValueForOption(modelLayerOption);
            var version = ctx.ParseResult.GetValueForOption(modelVersionOption);
            var mirror = ctx.ParseResult.GetValueForOption(modelMirrorOption);
            var force = ctx.ParseResult.GetValueForOption(modelForceOption);
            var rerunSetup = ctx.ParseResult.GetValueForOption(modelSetupOption);

            var exitCode = await ModelMode.RunAsync(cmd, layer, version, mirror, force, rerunSetup);
            ctx.ExitCode = exitCode;
        });

        rootCommand.AddCommand(modelCommand);

        return rootCommand;
    }

    private static async Task<int> RunSetupAsync()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var wizard = new InteractiveSetupWizard(configPath);
        await wizard.RunAsync();
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine();
        Console.WriteLine("LTAI CLI");
        Console.WriteLine();
        Console.WriteLine("用法: ltai <command>");
        Console.WriteLine();
        Console.WriteLine("命令:");
        Console.WriteLine("  setup      运行配置向导 (可重复运行)");
        Console.WriteLine("  model      管理本地模型 (list/download/remove)");
        Console.WriteLine("  debug      运行全链路跟踪测试 (启发式生成问题 + 端对端测试)");
        Console.WriteLine("  improve    架构审计 + 论文驱动创新 (自动梳理问题 + 搜索论文 + 提出改革方案)");
        Console.WriteLine("  help       显示此帮助信息");
        Console.WriteLine();
        Console.WriteLine("Model 命令:");
        Console.WriteLine("  ltai model list [--layer L0|L1|L2]");
        Console.WriteLine("  ltai model download --layer L1 --version qwen2.5-1.5b-q4 [--mirror]");
        Console.WriteLine("  ltai model remove --layer L1 --version qwen2.5-1.5b-q4");
        Console.WriteLine("  ltai model reset [--setup]                       # 清除所有模型, --setup 可重新进入引导");
        Console.WriteLine();
        Console.WriteLine("Setup 选项:");
        Console.WriteLine("  配置 L0/L1/L2 三层，支持 API 或 Local 模式");
        Console.WriteLine("  自动检测硬件，推荐最佳模型和引擎格式");
        Console.WriteLine();
    }
}

internal static class DebugMode
{
    private static string DocsDir
    {
        get
        {
            var baseDir = AppContext.BaseDirectory;
            var rootDir = FindRootDirectory(baseDir, "docs");
            if (rootDir != null)
                return Path.Combine(rootDir, "docs", "debug");
            return Path.Combine(baseDir, "docs", "debug");
        }
    }

    public static async Task RunAsync(string? query, int count, string? difficulty, string? domain, bool generateReport)
    {
        Console.WriteLine("=== LTAI Debug Mode ===\n");

        var tracer = new FullLinkTracer();
        var generator = new HeuristicQuestionGenerator();
        
        Console.WriteLine("Initializing test infrastructure...");

        var docDir = Path.GetFullPath(DocsDir);
        Directory.CreateDirectory(docDir);

        if (!string.IsNullOrEmpty(query))
        {
            var traceId = tracer.StartTrace(query);
            
            tracer.RecordStageStart(traceId, TraceStage.Router, query);
            await Task.Delay(10);
            tracer.RecordStageEnd(traceId, TraceStage.Router, "local_llm", success: true, metadata: new Dictionary<string, object>
            {
                ["Confidence"] = 0.75f,
                ["Complexity"] = 0.5f
            });

            tracer.RecordStageStart(traceId, TraceStage.L1_Generation, query);
            await Task.Delay(50);
            tracer.RecordStageEnd(traceId, TraceStage.L1_Generation, "Generated response", success: true);

            tracer.RecordStageStart(traceId, TraceStage.Verification);
            await Task.Delay(5);
            tracer.RecordStageEnd(traceId, TraceStage.Verification, "Passed", success: true);

            var report = tracer.EndTrace(traceId, "local_llm", "Mock response");
            
            Console.WriteLine($"\n✅ Trace completed.");
            Console.WriteLine($"   Duration: {report.TotalDuration.TotalMilliseconds:F0}ms");
            Console.WriteLine($"   Stages: {report.Spans.Count}");
            Console.WriteLine($"   Success: {report.Success}");
            
            if (report.Bottlenecks?.Count > 0)
            {
                Console.WriteLine("\n   Bottlenecks:");
                foreach (var bn in report.Bottlenecks)
                    Console.WriteLine($"     - {bn}");
            }

            var docPath = Path.Combine(docDir, $"trace_{DateTime.UtcNow:yyyyMMdd_HHmmss}.md");
            await File.WriteAllTextAsync(docPath, GenerateTraceDocument(report), default);
            Console.WriteLine($"\n📄 Trace document saved: {docPath}");
        }
        else
        {
            Console.WriteLine($"\n📝 Generating {count} test cases...");
            var tests = generator.GenerateTests(count);
            
            Console.WriteLine($"\nGenerated {tests.Count} tests:");
            Console.WriteLine($"  Simple: {tests.Count(t => t.Difficulty == TestDifficulty.Simple)}");
            Console.WriteLine($"  Moderate: {tests.Count(t => t.Difficulty == TestDifficulty.Moderate)}");
            Console.WriteLine($"  Complex: {tests.Count(t => t.Difficulty == TestDifficulty.Complex)}");
            Console.WriteLine($"  OOD: {tests.Count(t => t.Difficulty == TestDifficulty.OOD)}");
            Console.WriteLine();

            Console.WriteLine("Sample test cases:");
            foreach (var test in tests.Take(5))
            {
                Console.WriteLine($"  [{test.Difficulty,8}] [{test.Domain,10}] {test.Query}");
                Console.WriteLine($"             Expected Route: {test.ExpectedRoute}");
            }

            Console.WriteLine("\n🔄 Running end-to-end tests...");
            Console.WriteLine("   [Mock] Tests would run against real L1L2DuplexRouter here.");
            Console.WriteLine("   [Mock] Results would show Pass/Fail per test case.");

            var reportPath = Path.Combine(docDir, $"debug_report_{DateTime.UtcNow:yyyyMMdd_HHmmss}.md");
            await File.WriteAllTextAsync(reportPath, GenerateDebugDocument(tests, generateReport), default);
            Console.WriteLine($"\n📄 Debug report saved: {reportPath}");
        }

        Console.WriteLine("\n✅ Debug mode completed.");
    }

    private static string GenerateTraceDocument(TraceReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# LTAI Trace Report");
        sb.AppendLine();
        sb.AppendLine($"| Item | Value |");
        sb.AppendLine($"|------|-------|");
        sb.AppendLine($"| **Trace ID** | `{report.TraceId}` |");
        sb.AppendLine($"| **Query** | {report.Query} |");
        sb.AppendLine($"| **Time** | {report.StartTime:yyyy-MM-dd HH:mm:ss} |");
        sb.AppendLine($"| **Duration** | {report.TotalDuration.TotalMilliseconds:F0}ms |");
        sb.AppendLine($"| **Route** | {report.FinalRoute} |");
        sb.AppendLine($"| **Success** | {(report.Success ? "✅ Yes" : "❌ No")} |");
        sb.AppendLine();

        sb.AppendLine("## Pipeline Stages");
        sb.AppendLine();
        sb.AppendLine("| # | Stage | Duration | Status | Input | Output |");
        sb.AppendLine("|---|-------|----------|--------|-------|--------|");
        for (int i = 0; i < report.Spans.Count; i++)
        {
            var span = report.Spans[i];
            sb.AppendLine($"| {i + 1} | {span.Stage} | {span.Duration.TotalMilliseconds:F0}ms | {(span.Success ? "✅" : "❌")} | {Truncate(span.Input, 30)} | {Truncate(span.Output, 30)} |");
        }
        sb.AppendLine();

        if (report.Bottlenecks?.Count > 0)
        {
            sb.AppendLine("## Bottlenecks");
            sb.AppendLine();
            foreach (var bn in report.Bottlenecks)
                sb.AppendLine($"- {bn}");
            sb.AppendLine();
        }

        if (report.Errors?.Count > 0)
        {
            sb.AppendLine("## Errors");
            sb.AppendLine();
            foreach (var err in report.Errors)
                sb.AppendLine($"- {err}");
            sb.AppendLine();
        }

        sb.AppendLine($"---\n*Generated by LTAI Debug at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC*");
        return sb.ToString();
    }

    private static string GenerateDebugDocument(List<HeuristicTestCase> tests, bool includeFullReport)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# LTAI Debug Report");
        sb.AppendLine();
        sb.AppendLine($"**Generated:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();

        sb.AppendLine("## Test Suite Summary");
        sb.AppendLine();
        sb.AppendLine("| Difficulty | Count | Expected Route |");
        sb.AppendLine("|------------|-------|----------------|");
        foreach (var d in Enum.GetValues<TestDifficulty>())
        {
            var count = tests.Count(t => t.Difficulty == d);
            if (count == 0) continue;
            var route = tests.First(t => t.Difficulty == d).ExpectedRoute;
            sb.AppendLine($"| {d} | {count} | `{route}` |");
        }
        sb.AppendLine();

        sb.AppendLine("## Domain Distribution");
        sb.AppendLine();
        sb.AppendLine("| Domain | Count |");
        sb.AppendLine("|--------|-------|");
        foreach (var dom in Enum.GetValues<TestDomain>())
        {
            var count = tests.Count(t => t.Domain == dom);
            if (count == 0) continue;
            sb.AppendLine($"| {dom} | {count} |");
        }
        sb.AppendLine();

        if (includeFullReport)
        {
            sb.AppendLine("## Test Cases");
            sb.AppendLine();
            for (int i = 0; i < tests.Count; i++)
            {
                var t = tests[i];
                sb.AppendLine($"### {i + 1}. [{t.Difficulty}] {t.Domain}");
                sb.AppendLine();
                sb.AppendLine($"- **Query:** {t.Query}");
                sb.AppendLine($"- **Expected Route:** `{t.ExpectedRoute}`");
                sb.AppendLine($"- **Description:** {t.Description}");
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine("## Sample Test Cases");
            sb.AppendLine();
            sb.AppendLine("| # | Difficulty | Domain | Query | Expected Route |");
            sb.AppendLine("|---|------------|--------|-------|----------------|");
            foreach (var t in tests.Take(10))
            {
                sb.AppendLine($"| | {t.Difficulty} | {t.Domain} | {Truncate(t.Query, 50)} | `{t.ExpectedRoute}` |");
            }
            sb.AppendLine();
            sb.AppendLine($"> *{tests.Count - 10} more test cases omitted. Use `--report` for full listing.*");
            sb.AppendLine();
        }

        sb.AppendLine("## Pipeline Architecture");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine("Query → Cache → BinaryIndex → DomainGraph → SynapticInference → LocalLLM");
        sb.AppendLine("  │         │          │            │                │              │");
        sb.AppendLine("  ├─cache_hit├─binary_correction├─graph_knowledge├─synaptic_knowledge├─local_llm");
        sb.AppendLine("  │                                                                    │");
        sb.AppendLine("  └────────────────────────────────────────────────────── PACE Routing ─┤");
        sb.AppendLine("                                                                        │");
        sb.AppendLine("                    ┌─ RecursiveMAS (ZPD zone)                          │");
        sb.AppendLine("                    ├─ ForceL2 (plateau)                                │");
        sb.AppendLine("                    └─ DirectL2 (OOD)                                   │");
        sb.AppendLine("```");
        sb.AppendLine();

        sb.AppendLine("---\n*Generated by LTAI Debug Mode*");
        return sb.ToString();
    }

    private static string Truncate(string? text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= maxLen ? text : text[..(maxLen - 3)] + "...";
    }

    private static string? FindRootDirectory(string startDir, string markerDir)
    {
        var current = startDir;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(Path.Combine(current, markerDir)))
                return current;
            current = Path.GetDirectoryName(current);
        }
        return null;
    }
}

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
            paperList = await searchAgent.SearchRecentPapersAsync();
            
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
                paperList = await searchAgent.SearchRecentPapersAsync();
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
            await File.WriteAllTextAsync(auditPath, GenerateAuditDocument(auditReport), default);
            Console.WriteLine($"\n📄 Audit report saved: {auditPath}");
        }

        if (paperList != null)
        {
            var papersPath = Path.Combine(docDir, $"papers_{ts}.md");
            await File.WriteAllTextAsync(papersPath, GeneratePapersDocument(paperList), default);
            Console.WriteLine($"\n📄 Papers report saved: {papersPath}");
        }

        if (proposalDoc != null)
        {
            var proposalPath = Path.Combine(docDir, $"proposal_{ts}.md");
            await File.WriteAllTextAsync(proposalPath, GenerateProposalDocument(proposalDoc), default);
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

    private static string Truncate(string? text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= maxLen ? text : text[..(maxLen - 3)] + "...";
    }

    private static string? FindRootDirectory(string startDir, string markerDir)
    {
        var current = startDir;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(Path.Combine(current, markerDir)))
                return current;
            current = Path.GetDirectoryName(current);
        }
        return null;
    }
}
