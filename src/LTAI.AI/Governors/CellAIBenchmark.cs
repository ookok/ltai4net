using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed record BenchmarkResult
{
    public string Domain { get; init; } = "";
    public int QueryCount { get; init; }
    public float AvgLatencyMs { get; init; }
    public float P95LatencyMs { get; init; }
    public float P99LatencyMs { get; init; }
    public float Accuracy { get; init; }
    public float MemoryMB { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public sealed class CellAIBenchmark
{
    private readonly CellAIRegistry _registry;
    private readonly CellAnswerStore _answerStore;
    private readonly ILogger<CellAIBenchmark> _logger;

    public CellAIBenchmark(
        CellAIRegistry registry,
        CellAnswerStore answerStore,
        ILogger<CellAIBenchmark>? logger = null)
    {
        _registry = registry;
        _answerStore = answerStore;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CellAIBenchmark>.Instance;
    }

    public BenchmarkResult RunBenchmark(string domain, List<string> testQueries, int iterations = 3)
    {
        var latencies = new List<float>();
        var correctCount = 0;
        var totalQueries = 0;

        for (int iter = 0; iter < iterations; iter++)
        {
            foreach (var query in testQueries)
            {
                totalQueries++;
                var stopwatch = Stopwatch.StartNew();
                var cellResult = _registry.TryActivateCell(query);
                stopwatch.Stop();

                latencies.Add((float)stopwatch.Elapsed.TotalMilliseconds);

                if (cellResult.Activated && !string.IsNullOrEmpty(cellResult.Response))
                {
                    correctCount++;
                }
            }
        }

        latencies.Sort();
        var avgLatency = latencies.Count > 0 ? latencies.Average() : 0f;
        var p95Latency = latencies.Count > 0 ? latencies[(int)(latencies.Count * 0.95)] : 0f;
        var p99Latency = latencies.Count > 0 ? latencies[(int)(latencies.Count * 0.99)] : 0f;
        var accuracy = totalQueries > 0 ? (float)correctCount / totalQueries : 0f;

        var cellMetrics = _registry.GetMetrics();
        var memoryMB = cellMetrics.TryGetValue("total_memory_mb", out var mem) ? (float)(double)mem : 0f;

        var benchmarkResult = new BenchmarkResult
        {
            Domain = domain,
            QueryCount = totalQueries,
            AvgLatencyMs = (float)Math.Round(avgLatency, 2),
            P95LatencyMs = (float)Math.Round(p95Latency, 2),
            P99LatencyMs = (float)Math.Round(p99Latency, 2),
            Accuracy = (float)Math.Round(accuracy, 3),
            MemoryMB = (float)Math.Round(memoryMB, 2)
        };

        _logger.LogInformation(
            "Benchmark [{Domain}]: queries={Queries} avg={Avg:F2}ms p95={P95:F2}ms p99={P99:F2}ms accuracy={Acc:F3} memory={Mem:F1}MB",
            benchmarkResult.Domain, benchmarkResult.QueryCount, benchmarkResult.AvgLatencyMs, benchmarkResult.P95LatencyMs,
            benchmarkResult.P99LatencyMs, benchmarkResult.Accuracy, benchmarkResult.MemoryMB);

        return benchmarkResult;
    }

    public Dictionary<string, BenchmarkResult> RunAllBenchmarks(Dictionary<string, List<string>> testSets)
    {
        var results = new Dictionary<string, BenchmarkResult>();

        foreach (var (domain, queries) in testSets)
        {
            results[domain] = RunBenchmark(domain, queries);
        }

        return results;
    }
}
