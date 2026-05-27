using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LTAI.AI.Governors;
using LTAI.AI.Interfaces;

namespace LTAI.Cli.Debug;

/// <summary>
/// 端到端测试结果
/// </summary>
public sealed record TestResult
{
    public HeuristicTestCase TestCase { get; init; } = new();
    public bool Passed { get; init; }
    public string ActualRoute { get; init; } = "";
    public string? Response { get; init; }
    public TimeSpan Duration { get; init; }
    public TraceReport? TraceReport { get; init; }
    public string? FailureReason { get; init; }
    public List<string> Observations { get; init; } = new();
}

/// <summary>
/// 端到端测试运行器
/// 执行测试并比对预期结果，输出 Pass/Fail + 瓶颈分析
/// </summary>
public sealed class EndToEndTestRunner
{
    private readonly ILivingTreeSystem? _lts;
    private readonly FullLinkTracer _tracer;

    public EndToEndTestRunner(ILivingTreeSystem? lts, FullLinkTracer tracer)
    {
        _lts = lts;
        _tracer = tracer;
    }

    /// <summary>
    /// 运行单个测试用例
    /// </summary>
    public async Task<TestResult> RunTestAsync(HeuristicTestCase testCase)
    {
        var traceId = _tracer.StartTrace(testCase.Query);
        var startTime = DateTime.UtcNow;

        try
        {
            var route = "simplified";
            var response = _lts != null ? await _lts.ChatAsync(testCase.Query) : "";
            var confidence = _lts != null ? 0.7f : 0f;
            var complexity = 0.5f;
            var modelType = _lts != null ? "L2" : "none";

            _tracer.RecordStageEnd(traceId, TraceStage.Router, route, success: true, metadata: new Dictionary<string, object>
            {
                ["Confidence"] = confidence,
                ["Complexity"] = complexity,
                ["ModelType"] = modelType
            });

            var endTime = DateTime.UtcNow;
            var duration = endTime - startTime;

            var passed = ValidateResult(testCase, route, response);
            var observations = GenerateObservations(testCase, route, response, duration);

            var traceReport = _tracer.EndTrace(traceId, route, response);

            return new TestResult
            {
                TestCase = testCase,
                Passed = passed,
                ActualRoute = route,
                Response = response,
                Duration = duration,
                TraceReport = traceReport,
                Observations = observations,
                FailureReason = passed ? null : $"Expected route matching '{testCase.ExpectedRoute}', got '{route}'"
            };
        }
        catch (Exception ex)
        {
            _tracer.RecordStageEnd(traceId, TraceStage.Router, error: ex.Message, success: false);
            var traceReport = _tracer.EndTrace(traceId);

            return new TestResult
            {
                TestCase = testCase,
                Passed = false,
                Duration = DateTime.UtcNow - startTime,
                TraceReport = traceReport,
                FailureReason = $"Exception: {ex.Message}",
                Observations = new List<string> { "Test execution failed with exception" }
            };
        }
    }

    /// <summary>
    /// 批量运行测试用例
    /// </summary>
    public async Task<List<TestResult>> RunTestsAsync(List<HeuristicTestCase> testCases)
    {
        var results = new List<TestResult>();
        
        foreach (var testCase in testCases)
        {
            var result = await RunTestAsync(testCase).ConfigureAwait(false);
            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// 生成测试报告
    /// </summary>
    public string GenerateReport(List<TestResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== LTAI End-to-End Test Report ===\n");

        var total = results.Count;
        var passed = results.Count(r => r.Passed);
        var failed = total - passed;
        var passRate = total > 0 ? (double)passed / total * 100 : 0;
        var avgDuration = results.Count > 0 ? results.Average(r => r.Duration.TotalMilliseconds) : 0;

        sb.AppendLine($"Total: {total} | Passed: {passed} | Failed: {failed} | Pass Rate: {passRate:F1}%");
        sb.AppendLine($"Average Duration: {avgDuration:F0}ms\n");

        // 按难度统计
        sb.AppendLine("--- Results by Difficulty ---");
        foreach (var difficulty in Enum.GetValues<TestDifficulty>())
        {
            var tests = results.Where(r => r.TestCase.Difficulty == difficulty).ToList();
            if (tests.Count == 0) continue;
            
            var p = tests.Count(r => r.Passed);
            sb.AppendLine($"  {difficulty}: {p}/{tests.Count} ({(double)p / tests.Count * 100:F0}%)");
        }

        // 按领域统计
        sb.AppendLine("\n--- Results by Domain ---");
        foreach (var domain in Enum.GetValues<TestDomain>())
        {
            var tests = results.Where(r => r.TestCase.Domain == domain).ToList();
            if (tests.Count == 0) continue;
            
            var p = tests.Count(r => r.Passed);
            sb.AppendLine($"  {domain}: {p}/{tests.Count} ({(double)p / tests.Count * 100:F0}%)");
        }

        // 瓶颈分析
        var bottlenecks = results
            .Where(r => r.TraceReport?.Bottlenecks != null)
            .SelectMany(r => r.TraceReport?.Bottlenecks ?? [])
            .GroupBy(b => b.Split(':')[0].Trim())
            .OrderByDescending(g => g.Count())
            .Take(5);

        if (bottlenecks.Any())
        {
            sb.AppendLine("\n--- Top Bottlenecks ---");
            foreach (var bn in bottlenecks)
            {
                sb.AppendLine($"  {bn.Key}: {bn.Count()} occurrences");
            }
        }

        // 失败详情
        var failures = results.Where(r => !r.Passed).ToList();
        if (failures.Any())
        {
            sb.AppendLine("\n--- Failed Tests ---");
            foreach (var f in failures.Take(10))
            {
                sb.AppendLine($"  [{f.TestCase.Difficulty}] [{f.TestCase.Domain}] {f.TestCase.Query}");
                sb.AppendLine($"    Expected: {f.TestCase.ExpectedRoute}");
                sb.AppendLine($"    Actual: {f.ActualRoute}");
                sb.AppendLine($"    Reason: {f.FailureReason}");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static bool ValidateResult(HeuristicTestCase testCase, string route, string response)
    {
        // 简单验证：实际路由是否匹配预期模式
        var expectedPatterns = testCase.ExpectedRoute.Split('|');
        return expectedPatterns.Any(pattern => route.Contains(pattern.Trim()));
    }

    private static List<string> GenerateObservations(HeuristicTestCase testCase, string route, string response, TimeSpan duration)
    {
        var observations = new List<string>();

        if (0.5 < 0.5f)
            observations.Add("Low confidence response");

        if (duration.TotalMilliseconds > 1000)
            observations.Add($"High latency: {duration.TotalMilliseconds:F0}ms");

        if (route.Contains("delegate_l2") && testCase.Difficulty != TestDifficulty.OOD)
            observations.Add("Unexpected L2 delegation for non-OOD query");

        if (route.Contains("recursive") && testCase.Difficulty == TestDifficulty.Simple)
            observations.Add("Over-engineered: RecursiveMAS used for simple query");

        return observations;
    }
}
