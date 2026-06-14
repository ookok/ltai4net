using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Tools;

public enum ToolErrorType
{
    NotFound,
    ExecutionFailed,
    PermissionDenied,
    Timeout,
}

public enum RecoveryAction
{
    Retry,
    Substitute,
    NotifyUser,
    Abort,
}

public sealed record ToolRecoveryResult(
    RecoveryAction Action,
    string? Message = null,
    AITool? SubstituteTool = null,
    string? SubstituteArgs = null);

public sealed class ToolErrorRecoveryHandler
{
    private readonly IReadOnlyList<AITool> _allTools;
    private readonly ILogger<ToolErrorRecoveryHandler> _logger;
    private readonly Dictionary<string, int> _consecutiveErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private const int MaxConsecutiveBeforeAbort = 2;

    public ToolErrorRecoveryHandler(IReadOnlyList<AITool> allTools,
        ILogger<ToolErrorRecoveryHandler>? logger = null)
    {
        _allTools = allTools;
        _logger = logger;
    }

    public ToolRecoveryResult Recover(string toolName, string arguments, string errorMessage)
    {
        var errorType = ClassifyError(errorMessage);
        _logger?.LogDebug("Tool error: {Name} failed with {ErrorType}: {Msg}", toolName, errorType, errorMessage);

        var key = $"{toolName}:{errorType}";
        int count;
        lock (_lock)
        {
            _consecutiveErrors.TryGetValue(key, out count);
            _consecutiveErrors[key] = count + 1;
        }

        if (count >= MaxConsecutiveBeforeAbort)
            return new ToolRecoveryResult(RecoveryAction.Abort,
                $"工具 {toolName} 连续失败 {count + 1} 次，已放弃");

        return errorType switch
        {
            ToolErrorType.NotFound => HandleNotFound(toolName, arguments),
            ToolErrorType.ExecutionFailed => HandleExecutionFailed(toolName, arguments),
            ToolErrorType.PermissionDenied => new ToolRecoveryResult(RecoveryAction.NotifyUser,
                $"工具 {toolName} 权限不足，请检查配置"),
            ToolErrorType.Timeout => new ToolRecoveryResult(RecoveryAction.Retry,
                $"工具 {toolName} 超时，正在重试"),
            _ => new ToolRecoveryResult(RecoveryAction.Retry),
        };
    }

    public void RecordSuccess(string toolName)
    {
        lock (_lock)
        {
            var keys = _consecutiveErrors.Keys
                .Where(k => k.StartsWith(toolName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var k in keys) _consecutiveErrors.Remove(k);
        }
    }

    private static ToolErrorType ClassifyError(string message)
    {
        if (string.IsNullOrEmpty(message)) return ToolErrorType.ExecutionFailed;
        var lower = message.ToLowerInvariant();
        if (lower.Contains("not found") || lower.Contains("不存在") || lower.Contains("未知工具"))
            return ToolErrorType.NotFound;
        if (lower.Contains("permission") || lower.Contains("denied") || lower.Contains("权限不足") || lower.Contains("forbidden"))
            return ToolErrorType.PermissionDenied;
        if (lower.Contains("timeout") || lower.Contains("timed out") || lower.Contains("超时"))
            return ToolErrorType.Timeout;
        return ToolErrorType.ExecutionFailed;
    }

    private ToolRecoveryResult HandleNotFound(string toolName, string arguments)
    {
        var fuzzy = _allTools
            .Select(t => new
            {
                Tool = t,
                Score = FuzzyMatchScore(toolName, t.Name ?? "")
            })
            .Where(x => x.Score > 0.5)
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();

        if (fuzzy != null)
            return new ToolRecoveryResult(RecoveryAction.Substitute,
                $"工具 {toolName} 未找到，尝试使用 {fuzzy.Tool.Name}",
                fuzzy.Tool, arguments);

        return new ToolRecoveryResult(RecoveryAction.NotifyUser,
            $"工具 {toolName} 不存在");
    }

    private ToolRecoveryResult HandleExecutionFailed(string toolName, string arguments)
    {
        return new ToolRecoveryResult(RecoveryAction.Retry,
            $"工具 {toolName} 执行失败，正在重试");
    }

    private static double FuzzyMatchScore(string input, string target)
    {
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(target)) return 0;
        var inputLower = input.ToLowerInvariant();
        var targetLower = target.ToLowerInvariant();

        if (targetLower.Contains(inputLower)) return 0.9;
        if (inputLower.Contains(targetLower)) return 0.8;

        var distance = Levenshtein(inputLower, targetLower);
        var maxLen = Math.Max(inputLower.Length, targetLower.Length);
        if (maxLen == 0) return 0;
        return 1.0 - (double)distance / maxLen;
    }

    private static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
