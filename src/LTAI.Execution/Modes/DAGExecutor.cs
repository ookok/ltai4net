using System.Diagnostics;
using LTAI.Core.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Execution.Modes;

public sealed class DAGExecutor
{
    private readonly SemaphoreSlim _semaphore;
    private readonly ILogger<DAGExecutor> _logger;

    public DAGExecutor(int maxParallel = 5)
    {
        _semaphore = new SemaphoreSlim(maxParallel);
        _logger = NullLogger.Instance;
    }

    internal DAGExecutor(int maxParallel, ILogger<DAGExecutor> logger)
    {
        _semaphore = new SemaphoreSlim(maxParallel);
        _logger = logger;
    }

    public async Task<List<Dictionary<string, object?>>> Execute(
        List<Dictionary<string, object?>> plan,
        Func<Dictionary<string, object?>, CancellationToken, Task<object?>> executeOne,
        object? ctx = null,
        bool fold = false,
        int foldMaxChars = 500,
        CancellationToken ct = default)
    {
        var results = new Dictionary<string, object?>[plan.Count];
        var completed = new HashSet<int>();
        var running = new HashSet<int>();
        var tasks = new Dictionary<int, Task>();

        while (completed.Count < plan.Count)
        {
            ct.ThrowIfCancellationRequested();

            var ready = FindReadySteps(plan, completed, running);

            if (ready.Count == 0 && running.Count == 0 && completed.Count < plan.Count)
            {
                var nextPending = Enumerable.Range(0, plan.Count)
                    .FirstOrDefault(i => !completed.Contains(i) && !running.Contains(i));

                if (completed.Count > 0 || nextPending > 0)
                {
                    ready.Add(nextPending);
                    _logger.LogWarning("DAG deadlock avoidance: forcing step {Step}", nextPending);
                }
            }

            foreach (var idx in ready)
            {
                if (running.Count >= _semaphore.CurrentCount + running.Count)
                    break;

                await _semaphore.WaitAsync(ct);
                running.Add(idx);

                var step = plan[idx];
                var capturedIdx = idx;
                var task = Task.Run(async () =>
                {
                    try
                    {
                        var sw = Stopwatch.GetTimestamp();
                        var result = await executeOne(step, ct);
                        var latency = (Stopwatch.GetTimestamp() - sw) / (double)Stopwatch.Frequency * 1000.0;

                        results[capturedIdx] = new Dictionary<string, object?>
                        {
                            ["name"] = step.GetValueOrDefault("name"),
                            ["result"] = result,
                            ["latency_ms"] = latency,
                            ["status"] = "completed"
                        };

                        _logger.LogDebug("DAG step {Step} completed in {Latency:F0}ms", capturedIdx, latency);
                    }
                    catch (Exception ex)
                    {
                        results[capturedIdx] = new Dictionary<string, object?>
                        {
                            ["name"] = step.GetValueOrDefault("name"),
                            ["error"] = ex.Message,
                            ["status"] = "failed"
                        };

                        _logger.LogError(ex, "DAG step {Step} failed", capturedIdx);
                    }
                    finally
                    {
                        lock (completed)
                        {
                            completed.Add(capturedIdx);
                            running.Remove(capturedIdx);
                        }

                        _semaphore.Release();
                    }
                }, ct);

                tasks[capturedIdx] = task;
            }

            if (ready.Count == 0 && running.Count > 0)
            {
                var runningTasks = tasks.Where(kvp => running.Contains(kvp.Key))
                    .Select(kvp => kvp.Value)
                    .ToList();

                if (runningTasks.Count > 0)
                {
                    await Task.WhenAny(runningTasks);
                }
            }

            if (ready.Count == 0 && running.Count == 0 && completed.Count < plan.Count)
            {
                await Task.Delay(50, ct);
            }
        }

        var resultList = results.Where(r => r != null).ToList()!;

        if (fold)
            await FoldResults(resultList, foldMaxChars);

        return resultList;
    }

    public async Task FoldResults(List<Dictionary<string, object?>> results, int maxChars = 500)
    {
        foreach (var result in results)
        {
            if (result.TryGetValue("result", out var val) && val is string text && text.Length > maxChars)
            {
                result["result"] = ContextFolding.FoldTextHeuristic(text, maxChars);
            }
        }

        await Task.CompletedTask;
    }

    public string CompactContext(List<Dictionary<string, object?>> results)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## DAG Execution Summary");
        sb.AppendLine();

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var name = result.GetValueOrDefault("name")?.ToString() ?? $"Step {i}";
            var status = result.GetValueOrDefault("status")?.ToString() ?? "unknown";

            sb.AppendLine($"- **{name}**: {status}");

            if (result.TryGetValue("latency_ms", out var latency))
                sb.AppendLine($"  - Latency: {latency:F0}ms");

            if (result.TryGetValue("result", out var resVal) && resVal is string resText && resText.Length > 0)
            {
                var preview = resText.Length > 200 ? resText[..200] + "..." : resText;
                sb.AppendLine($"  - Result: {preview}");
            }

            if (result.TryGetValue("error", out var err) && err is string errText && errText.Length > 0)
                sb.AppendLine($"  - Error: {errText}");
        }

        return sb.ToString();
    }

    public static List<Dictionary<string, object?>> AddDependencies(List<Dictionary<string, object?>> plan)
    {
        var hasDependencies = plan.Any(step =>
            step.TryGetValue("depends_on", out var dep) && dep is List<int> list && list.Count > 0);

        if (hasDependencies)
            return plan;

        for (var i = 1; i < plan.Count; i++)
        {
            plan[i]["depends_on"] = new List<int> { i - 1 };
        }

        return plan;
    }

    private static HashSet<int> FindReadySteps(
        List<Dictionary<string, object?>> plan,
        HashSet<int> completed,
        HashSet<int> running)
    {
        var ready = new HashSet<int>();

        for (var i = 0; i < plan.Count; i++)
        {
            if (completed.Contains(i) || running.Contains(i))
                continue;

            var step = plan[i];
            List<int>? dependsOn = null;

            if (step.TryGetValue("depends_on", out var dep))
            {
                dependsOn = dep switch
                {
                    List<int> intList => intList,
                    List<object?> objList => objList
                        .Select(d => d is int di ? di : Convert.ToInt32(d))
                        .ToList(),
                    _ => null
                };
            }

            if (dependsOn == null || dependsOn.Count == 0 || dependsOn.All(d => completed.Contains(d)))
                ready.Add(i);
        }

        return ready;
    }

    private sealed class NullLogger : ILogger<DAGExecutor>
    {
        public static readonly NullLogger Instance = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
