// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════
//  TokenSavingsTracker — tracks tokens saved by graph/tool
//  lookups vs naive file reads (Gortex-inspired).
//
//  Metric: "how many tokens would a naive file read have
//  consumed vs what the graph/tool actually returned."
// ═══════════════════════════════════════════════════════

using System.Diagnostics.Metrics;

namespace LTAI.Core.Configuration;

public static class TokenSavingsTracker
{
    private static readonly Meter SavingsMeter = new("LTAI.TokenSavings");
    private static readonly Counter<long> MetricTokensSaved = SavingsMeter.CreateCounter<long>(
        "ltai.tokens.saved", "tokens", "Tokens saved by graph/tool lookups vs naive file reads");
    private static readonly Counter<long> MetricTokensNaive = SavingsMeter.CreateCounter<long>(
        "ltai.tokens.naive", "tokens", "Estimated tokens if naive file reads were used");
    private static readonly Counter<long> MetricTokensActual = SavingsMeter.CreateCounter<long>(
        "ltai.tokens.actual", "tokens", "Actual tokens consumed by graph/tool responses");
    private static readonly Counter<long> MetricLookups = SavingsMeter.CreateCounter<long>(
        "ltai.tokens.lookups", "calls", "Total lookups tracked");

    private static long _totalTokensSaved;
    private static long _totalTokensNaive;
    private static long _totalTokensActual;
    private static long _totalLookups;
    private static readonly object _lock = new();

    /// <summary>Total tokens saved across all tracked calls.</summary>
    public static long TotalTokensSaved => Volatile.Read(ref _totalTokensSaved);

    /// <summary>Total naive file-read tokens (counterfactual).</summary>
    public static long TotalTokensNaive => Volatile.Read(ref _totalTokensNaive);

    /// <summary>Total actual tokens consumed.</summary>
    public static long TotalTokensActual => Volatile.Read(ref _totalTokensActual);

    /// <summary>Total lookups tracked.</summary>
    public static long TotalLookups => Volatile.Read(ref _totalLookups);

    /// <summary>Savings ratio (0-1). 1.0 = 100% saved.</summary>
    public static double SavingsRatio => TotalTokensNaive > 0
        ? (double)TotalTokensSaved / TotalTokensNaive
        : 0;

    /// <summary>Average tokens saved per lookup.</summary>
    public static double AvgSavedPerLookup => TotalLookups > 0
        ? (double)TotalTokensSaved / TotalLookups
        : 0;

    /// <summary>
    /// Record a lookup.
    /// </summary>
    /// <param name="naiveTokens">Estimated tokens if a naive file read was used.</param>
    /// <param name="actualTokens">Actual tokens returned by the graph/tool.</param>
    public static void RecordLookup(int naiveTokens, int actualTokens)
    {
        var saved = Math.Max(0, naiveTokens - actualTokens);

        Interlocked.Increment(ref _totalLookups);
        Interlocked.Add(ref _totalTokensNaive, naiveTokens);
        Interlocked.Add(ref _totalTokensActual, actualTokens);
        Interlocked.Add(ref _totalTokensSaved, saved);

        MetricLookups.Add(1);
        MetricTokensNaive.Add(naiveTokens);
        MetricTokensActual.Add(actualTokens);
        MetricTokensSaved.Add(saved);
    }

    /// <summary>
    /// Convenience: estimate naive tokens from file size, record actual from tool output.
    /// </summary>
    public static void RecordFileLookup(string filePath, string toolOutput)
    {
        long naiveTokens;
        try
        {
            if (File.Exists(filePath))
            {
                var content = File.ReadAllText(filePath);
                naiveTokens = content.Length / 4; // ~4 chars per token
            }
            else
            {
                naiveTokens = 500; // default estimate for unknown files
            }
        }
        catch
        {
            naiveTokens = 500;
        }

        var actualTokens = Math.Max(1, toolOutput.Length / 4);
        RecordLookup((int)Math.Min(naiveTokens, int.MaxValue), (int)Math.Min(actualTokens, int.MaxValue));
    }

    /// <summary>Reset all counters.</summary>
    public static void Reset()
    {
        lock (_lock)
        {
            Interlocked.Exchange(ref _totalTokensSaved, 0);
            Interlocked.Exchange(ref _totalTokensNaive, 0);
            Interlocked.Exchange(ref _totalTokensActual, 0);
            Interlocked.Exchange(ref _totalLookups, 0);
        }
    }

    /// <summary>Human-readable summary.</summary>
    public static string Summary
    {
        get
        {
            var saved = TotalTokensSaved;
            var naive = TotalTokensNaive;
            var pct = SavingsRatio;
            var avg = AvgSavedPerLookup;
            var usd = saved * 3e-6; // rough: $3/M tokens for Claude
            return $"Tokens saved: {saved:N0} / {naive:N0} ({pct:P1}) · {TotalLookups:N0} lookups · avg {avg:F0}/call · ~${usd:F2}";
        }
    }
}
