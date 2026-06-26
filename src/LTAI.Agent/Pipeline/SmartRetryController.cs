using System.Collections.Concurrent;

namespace LTAI.Agent.Pipeline;

public enum RetryAction
{
    Continue,
    Stop,
    RevertAndStop,
}

public sealed record RetryDecision(RetryAction Action, string? Reason = null)
{
    public static RetryDecision Continue() => new(RetryAction.Continue);
    public static RetryDecision Stop(string reason) => new(RetryAction.Stop, reason);
    public static RetryDecision RevertAndStop(string? reason = null) => new(RetryAction.RevertAndStop, reason);
}

public sealed record GrammarCheckResult(
    string ErrorType,
    string FilePath,
    int ErrorCount,
    int ErrorsFixed,
    int NewErrorsIntroduced);

public sealed class SmartRetryController
{
    private readonly ConcurrentDictionary<(string ErrorType, string FilePath), int> _errorModeCounts = new();
    private int _previousErrorCount;
    private double _previousDifficulty;

    public RetryDecision Decide(GrammarCheckResult result, int attemptNumber)
    {
        var key = (result.ErrorType, result.FilePath);
        var count = _errorModeCounts.AddOrUpdate(key, 1, (_, v) => v + 1);

        if (count >= 2)
            return RetryDecision.Stop($"连续 {count} 次同类错误 ({result.ErrorType})，需切换策略");

        if (attemptNumber >= 2 && result.ErrorCount >= _previousErrorCount && _previousErrorCount > 0)
            return RetryDecision.Stop("错误数无下降趋势");

        if (result.NewErrorsIntroduced > result.ErrorsFixed)
            return RetryDecision.RevertAndStop("修复引入了新错误，回滚并停止");

        _previousErrorCount = result.ErrorCount;
        return RetryDecision.Continue();
    }

    /// <summary>Difficulty-aware retry decision (DeNovoSWE-inspired).
    /// Higher difficulty tasks get more retry budget; lower difficulty tasks
    /// are cut off earlier to avoid wasting cycles.</summary>
    public RetryDecision DecideWithDifficulty(
        GrammarCheckResult result, int attemptNumber, double difficulty, int maxAttempts)
    {
        // Adaptive budget: difficulty 0.8+ → full budget; 0.5-0.8 → 75%; <0.5 → 50%
        var budgetFactor = difficulty >= 0.8 ? 1.0 : difficulty >= 0.5 ? 0.75 : 0.5;
        var scaledMax = Math.Max(1, (int)(maxAttempts * budgetFactor));

        if (attemptNumber > scaledMax)
            return RetryDecision.Stop($"难度={difficulty:F2}，预算={scaledMax}次已用完");

        var key = (result.ErrorType, result.FilePath);
        var count = _errorModeCounts.AddOrUpdate(key, 1, (_, v) => v + 1);

        // For high-difficulty tasks, allow more persistent errors before stopping
        var persistThreshold = difficulty >= 0.8 ? 4 : difficulty >= 0.5 ? 3 : 2;
        if (count >= persistThreshold)
            return RetryDecision.Stop($"连续 {count} 次同类错误 ({result.ErrorType})，需切换策略 [难度={difficulty:F2}]");

        // Stagnation detection: errors may stabilize before improving on hard tasks
        if (difficulty < 0.5 && attemptNumber >= 2 &&
            result.ErrorCount >= _previousErrorCount && _previousErrorCount > 0)
            return RetryDecision.Stop("错误数无下降趋势 [低难度任务，快速中止]");

        if (result.NewErrorsIntroduced > result.ErrorsFixed)
            return RetryDecision.RevertAndStop("修复引入了新错误，回滚并停止");

        _previousErrorCount = result.ErrorCount;
        _previousDifficulty = difficulty;
        return RetryDecision.Continue();
    }

    public void RecordSuccess(string filePath)
    {
        var keys = _errorModeCounts.Keys
            .Where(k => k.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var key in keys)
            _errorModeCounts.TryRemove(key, out _);
        _previousErrorCount = 0;
        _previousDifficulty = 0;
    }

    public void Reset()
    {
        _errorModeCounts.Clear();
        _previousErrorCount = 0;
        _previousDifficulty = 0;
    }
}
