using System;
using System.IO;
using System.Text;
using LTAI.Cli.Debug;

namespace LTAI.Cli;

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

    private static string Truncate(string? text, int maxLen) => CliUtilities.Truncate(text, maxLen);
    private static string? FindRootDirectory(string startDir, string markerDir) => CliUtilities.FindRootDirectory(startDir, markerDir);
}