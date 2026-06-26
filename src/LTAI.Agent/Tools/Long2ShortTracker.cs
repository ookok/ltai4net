using System.Collections.Concurrent;
using LTAI.Core.Configuration;

namespace LTAI.Agent.Tools;

/// <summary>
/// Token efficiency tracker for tool responses.
/// Inspired by VibeThinker-3B's Long2Short Math RL:
/// among correct responses, shorter ones are preferred.
/// DI singleton. Replaceable for testing.
/// </summary>
public sealed class Long2ShortTracker
{
    private readonly ConcurrentDictionary<string, ToolOutputStats> _stats = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxTools = 200;

    /// <summary>Average tokens saved via conciseness.</summary>
    public long TotalTokensSaved { get; private set; }

    /// <summary>Record a tool's output and whether it was successful.</summary>
    public void RecordOutput(string toolName, string output, bool success)
    {
        if (_stats.Count >= MaxTools && !_stats.ContainsKey(toolName))
        {
            var oldest = _stats.Keys.FirstOrDefault();
            if (oldest != null) _stats.TryRemove(oldest, out _);
        }
        var tokens = TokenEstimator.Estimate(output); // CJK-aware
        var stat = _stats.GetOrAdd(toolName, _ => new ToolOutputStats());
        stat.AddSample(tokens, success);
    }

    /// <summary>
    /// Get the average output length for a tool (successful calls only).
    /// Returns 0 if no data.
    /// </summary>
    public int GetAverageLength(string toolName)
    {
        if (_stats.TryGetValue(toolName, out var stat))
            return stat.AverageLength;
        return 0;
    }

    /// <summary>
    /// Compute the "brevity bonus" for a tool's output.
    /// Positive = shorter than average (good). Negative = longer (could be optimized).
    /// </summary>
    public double GetBrevityScore(string toolName, string currentOutput)
    {
        var avg = GetAverageLength(toolName);
        if (avg <= 0) return 0;

        var currentTokens = TokenEstimator.Estimate(currentOutput);
        var diff = avg - currentTokens;

        if (avg == 0) return 0;
        return Math.Clamp((double)diff / avg * 2, -1.0, 1.0);
    }

    /// <summary>Human-readable summary.</summary>
    public string Summary
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
