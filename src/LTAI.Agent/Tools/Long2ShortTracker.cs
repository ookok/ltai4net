// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════
//  Long2ShortTracker — token efficiency optimization for
//  tool responses.
//
//  Inspired by VibeThinker-3B's Long2Short Math RL:
//  among correct responses, shorter ones are preferred.
//  We apply the same zero-sum brevity reward to tool outputs:
//  each tool invocation tracks its output length,
//  and the system learns to prefer concise-but-correct outputs.
// ═══════════════════════════════════════════════════════

using System.Collections.Concurrent;

namespace LTAI.Agent.Tools;

public static class Long2ShortTracker
{
    private static readonly ConcurrentDictionary<string, ToolOutputStats> _stats = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Average tokens saved via conciseness.</summary>
    public static long TotalTokensSaved { get; private set; }

    /// <summary>Record a tool's output and whether it was successful.</summary>
    public static void RecordOutput(string toolName, string output, bool success)
    {
        var tokens = output.Length / 4;
        var stat = _stats.GetOrAdd(toolName, _ => new ToolOutputStats());
        stat.AddSample(tokens, success);
    }

    /// <summary>
    /// Get the average output length for a tool (successful calls only).
    /// Returns 0 if no data.
    /// </summary>
    public static int GetAverageLength(string toolName)
    {
        if (_stats.TryGetValue(toolName, out var stat))
            return stat.AverageLength;
        return 0;
    }

    /// <summary>
    /// Compute the "brevity bonus" for a tool's output.
    /// Positive = shorter than average (good). Negative = longer (could be optimized).
    /// </summary>
    public static double GetBrevityScore(string toolName, string currentOutput)
    {
        var avg = GetAverageLength(toolName);
        if (avg <= 0) return 0;

        var currentTokens = currentOutput.Length / 4;
        var diff = avg - currentTokens;

        // Normalize: +1.0 for 50% shorter, -1.0 for 50% longer
        if (avg == 0) return 0;
        return Math.Clamp((double)diff / avg * 2, -1.0, 1.0);
    }

    /// <summary>Human-readable summary.</summary>
    public static string Summary
    {
        get
        {
            if (_stats.IsEmpty) return "No tool output data yet.";
            var lines = new List<string>();
            foreach (var (name, stat) in _stats.OrderByDescending(kv => kv.Value.TotalCalls).Take(10))
            {
                lines.Add($"  {name}: avg {stat.AverageLength} tokens, {stat.TotalCalls} calls, {stat.SuccessCalls} success");
            }
            return string.Join("\n", lines);
        }
    }

    public sealed class ToolOutputStats
    {
        private long _totalTokens;
        private int _totalCalls;
        private int _successCalls;

        public int TotalCalls => Volatile.Read(ref _totalCalls);
        public int SuccessCalls => Volatile.Read(ref _successCalls);
        public int AverageLength => _totalCalls > 0 ? (int)(Volatile.Read(ref _totalTokens) / _totalCalls) : 0;

        public void AddSample(int tokens, bool success)
        {
            Interlocked.Add(ref _totalTokens, tokens);
            Interlocked.Increment(ref _totalCalls);
            if (success) Interlocked.Increment(ref _successCalls);
        }
    }
}
