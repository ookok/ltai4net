using LTAI.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Tools;

// ============================================================================
// Parallel Tool Dispatcher — adapted from DeepSeek-Reasonix Pillar 1.
// Groups consecutive parallelSafe tools and executes them via Task.WhenAll.
// Tools without ParallelSafe (default) are executed sequentially.
// ============================================================================

/// <summary>
/// Result of a parallel tool dispatch batch.
/// </summary>
public sealed class ParallelDispatchResult
{
    public List<ToolDispatchItem> Results { get; init; } = new();
    public int BatchesExecuted { get; init; }
    public int ToolsParallelized { get; init; }
    public long ElapsedMs { get; init; }
}

/// <summary>
/// A single tool dispatch item with its result.
/// </summary>
public sealed class ToolDispatchItem
{
    public MkTool Tool { get; init; } = null!;
    public Dictionary<string, object?> Args { get; init; } = new();
    public object? Result { get; set; }
    public string? Error { get; set; }
    public bool Succeeded => Error == null;
}

/// <summary>
/// Dispatches tool calls, grouping consecutive parallel-safe tools into parallel batches.
/// </summary>
public sealed class ParallelToolDispatcher
{
    private readonly ILogger<ParallelToolDispatcher> _logger;

    public ParallelToolDispatcher(ILogger<ParallelToolDispatcher>? logger = null)
    {
        _logger = logger ?? NullLogger<ParallelToolDispatcher>.Instance;
    }

    /// <summary>
    /// Group tool dispatch items into batches: consecutive parallelSafe tools form one batch,
    /// non-parallelSafe tools each get their own batch. Batches execute sequentially;
    /// tools within a parallel-safe batch execute concurrently.
    /// </summary>
    public List<List<ToolDispatchItem>> GroupIntoBatches(List<ToolDispatchItem> items)
    {
        var batches = new List<List<ToolDispatchItem>>();
        List<ToolDispatchItem>? currentBatch = null;

        foreach (var item in items)
        {
            if (item.Tool.ParallelSafe)
            {
                // Add to current parallel batch (or start a new one)
                currentBatch ??= new List<ToolDispatchItem>();
                currentBatch.Add(item);
            }
            else
            {
                // Flush current parallel batch if any
                if (currentBatch != null && currentBatch.Count > 0)
                {
                    batches.Add(currentBatch);
                    currentBatch = null;
                }
                // Non-parallelSafe tool gets its own batch
                batches.Add(new List<ToolDispatchItem> { item });
            }
        }

        // Flush remaining parallel batch
        if (currentBatch != null && currentBatch.Count > 0)
            batches.Add(currentBatch);

        return batches;
    }

    /// <summary>
    /// Dispatch all items, executing parallel-safe batches concurrently.
    /// </summary>
    public async Task<ParallelDispatchResult> DispatchAsync(
        List<ToolDispatchItem> items,
        Func<MkTool, Dictionary<string, object?>, CancellationToken, Task<object?>> executor,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var batches = GroupIntoBatches(items);
        var toolsParallelized = 0;

        foreach (var batch in batches)
        {
            if (batch.Count == 1)
            {
                // Sequential: single tool
                var item = batch[0];
                try
                {
                    item.Result = await executor(item.Tool, item.Args, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    item.Error = ex.Message;
                }
            }
            else
            {
                // Parallel: multiple parallelSafe tools
                toolsParallelized += batch.Count;
                _logger.LogDebug(
                    "ParallelToolDispatcher: Executing {Count} tools in parallel", batch.Count);

                var tasks = batch.Select(async item =>
                {
                    try
                    {
                        item.Result = await executor(item.Tool, item.Args, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        item.Error = ex.Message;
                    }
                });

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
        }

        sw.Stop();
        return new ParallelDispatchResult
        {
            Results = items,
            BatchesExecuted = batches.Count,
            ToolsParallelized = toolsParallelized,
            ElapsedMs = sw.ElapsedMilliseconds
        };
    }
}
