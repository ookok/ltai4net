// Copyright (c) LTAI. All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LTAI.AI.Evaluation;

/// <summary>
/// Aggregate evaluation report for a batch of items.
/// </summary>
public sealed class EvalReport
{
    /// <summary>Per-item, per-check results.</summary>
    public List<EvalItemReport> Items { get; set; } = [];

    /// <summary>Total number of checks that passed.</summary>
    public int Passed => this.Items.Sum(i => i.Results.Count(r => r.Passed));

    /// <summary>Total number of checks that failed.</summary>
    public int Failed => this.Items.Sum(i => i.Results.Count(r => !r.Passed));

    /// <summary>Total number of checks run.</summary>
    public int Total => this.Items.Sum(i => i.Results.Count);

    /// <summary>Whether all checks passed across all items.</summary>
    public bool AllPassed => this.Failed == 0;

    /// <summary>Duration of the evaluation run.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>Returns a summary string.</summary>
    public string Summary()
    {
        return $"Evaluation: {this.Passed}/{this.Total} passed ({this.Duration.TotalSeconds:F1}s)"
               + (this.AllPassed ? " ✅" : $" ❌ ({this.Failed} failures)");
    }

    /// <summary>Returns a detailed markdown report.</summary>
    public string ToMarkdown()
    {
        var lines = new List<string>
        {
            $"# Evaluation Report",
            $"",
            $"**Summary:** {this.Passed}/{this.Total} passed in {this.Duration.TotalSeconds:F1}s",
            $"",
            $"| Item | Check | Result | Reason |",
            $"|------|-------|--------|--------|"
        };

        foreach (var item in this.Items)
        {
            foreach (var result in item.Results)
            {
                var icon = result.Passed ? "✅" : "❌";
                lines.Add($"| {item.ItemName} | {result.CheckName} | {icon} | {result.Reason} |");
            }
        }

        if (this.Failed > 0)
        {
            lines.Add("");
            lines.Add("## Failures");
            foreach (var item in this.Items)
            {
                foreach (var result in item.Results.Where(r => !r.Passed))
                {
                    lines.Add($"- **{item.ItemName}** / *{result.CheckName}*: {result.Reason}");
                }
            }
        }

        return string.Join("\n", lines);
    }
}

/// <summary>
/// Per-item evaluation result.
/// </summary>
public sealed class EvalItemReport
{
    /// <summary>Item name.</summary>
    public string ItemName { get; set; } = "";

    /// <summary>Per-check results.</summary>
    public List<EvalCheckResult> Results { get; set; } = [];

    /// <summary>Whether all checks passed for this item.</summary>
    public bool AllPassed => this.Results.All(r => r.Passed);
}

/// <summary>
/// Evaluation harness that runs items through checks and produces a report.
/// </summary>
public sealed class EvalHarness
{
    /// <summary>
    /// Runs the specified items through all specified checks.
    /// </summary>
    /// <param name="items">Items to evaluate.</param>
    /// <param name="checks">Checks to apply to each item.</param>
    /// <param name="parallel">Whether to evaluate items in parallel (default: false).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An <see cref="EvalReport"/> with per-item, per-check results.</returns>
    public async Task<EvalReport> RunAsync(
        IEnumerable<EvalItem> items,
        IEnumerable<EvalCheck> checks,
        bool parallel = false,
        CancellationToken cancellationToken = default)
    {
        var itemList = items.ToList();
        var checkList = checks.ToList();
        var stopwatch = Stopwatch.StartNew();

        var itemReports = new List<EvalItemReport>();

        if (parallel && itemList.Count > 1)
        {
            // Run items in parallel
            var tasks = itemList.Select(item =>
                EvaluateItemAsync(item, checkList, cancellationToken));
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            itemReports.AddRange(results);
        }
        else
        {
            foreach (var item in itemList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var report = await EvaluateItemAsync(item, checkList, cancellationToken).ConfigureAwait(false);
                itemReports.Add(report);
            }
        }

        stopwatch.Stop();

        return new EvalReport
        {
            Items = itemReports,
            Duration = stopwatch.Elapsed
        };
    }

    /// <summary>
    /// Evaluate a single item against a set of checks.
    /// </summary>
    private static async Task<EvalItemReport> EvaluateItemAsync(
        EvalItem item,
        List<EvalCheck> checks,
        CancellationToken cancellationToken)
    {
        var results = new List<EvalCheckResult>(checks.Count);

        foreach (var check in checks)
        {
            try
            {
                var result = await check(item, cancellationToken).ConfigureAwait(false);
                results.Add(result);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(new EvalCheckResult(false, $"Check threw: {ex.Message}", "runtime_error"));
            }
        }

        return new EvalItemReport
        {
            ItemName = item.Name,
            Results = results
        };
    }
}
