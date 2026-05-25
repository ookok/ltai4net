namespace LTAI.Core.Resilience;

/// <summary>
/// Unified error taxonomy for cross-component analysis.
/// Replaces ad-hoc error classification across DebugLoop, CorrectionMemory,
/// ToolCallRepairer, ResilienceBrain, and all other error handlers.
/// </summary>
public enum ErrorCategory
{
    BuildError,         // CSxxxx compilation errors
    RuntimeError,       // NullReference, ArgumentException, etc.
    NetworkError,       // HTTP timeout, DNS, socket failures
    BudgetError,        // Token/cost limit exceeded
    SafetyBlock,        // UnifiedSafetyGate blocked
    ToolFailure,        // Tool execution returned error
    ToolCallMalformed,  // LLM-generated bad JSON for tool call
    LLMError,           // LLM returned error/empty response
    MemoryError,        // Memory store corruption/loss
    ConcurrencyError,   // Deadlocks, race conditions, task failures
    TimeoutError,       // Operation timed out
    Unknown
}

public sealed record ErrorEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public ErrorCategory Category { get; init; }
    public string SourceComponent { get; init; } = "";
    public string Message { get; init; } = "";
    public string? StackTrace { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string? SessionId { get; init; }
    public bool WasRecovered { get; init; }
    public string? RecoveryStrategy { get; init; }
    public int AttemptCount { get; init; } = 1;

    public static ErrorEntry FromException(Exception ex, ErrorCategory category, string component, string? sessionId = null)
        => new()
        {
            Category = category,
            SourceComponent = component,
            Message = ex.Message,
            StackTrace = ex.StackTrace,
            SessionId = sessionId
        };

    public static ErrorEntry FromMessage(string message, ErrorCategory category, string component)
        => new() { Category = category, SourceComponent = component, Message = message };
}

/// <summary>
/// Cross-component error collector. All error handlers report here
/// for unified dashboards and cross-category analysis.
/// </summary>
public sealed class ErrorTaxonomy
{
    private readonly List<ErrorEntry> _entries = new();
    private readonly object _lock = new();

    public void Record(ErrorEntry entry)
    {
        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > 5000) _entries.RemoveRange(0, 2000);
        }
    }

    public List<ErrorEntry> GetRecent(int count = 50)
    {
        lock (_lock) return _entries.TakeLast(count).ToList();
    }

    public Dictionary<string, int> GetCategoryDistribution()
    {
        lock (_lock) return _entries
            .GroupBy(e => e.Category.ToString())
            .ToDictionary(g => g.Key, g => g.Count());
    }

    public double GetRecoveryRate()
    {
        lock (_lock)
        {
            if (_entries.Count == 0) return 1.0;
            return (double)_entries.Count(e => e.WasRecovered) / _entries.Count;
        }
    }

    public Dictionary<string, int> GetTopErrors(int topK = 10)
    {
        lock (_lock) return _entries
            .GroupBy(e => e.Message.Length > 80 ? e.Message[..80] : e.Message)
            .OrderByDescending(g => g.Count())
            .Take(topK)
            .ToDictionary(g => g.Key, g => g.Count());
    }
}

/// <summary>
/// Category mapping helpers — map component exceptions to ErrorCategory.
/// </summary>
public static class ErrorCategoryMapper
{
    public static ErrorCategory Classify(Exception ex) => ex switch
    {
        OperationCanceledException or TaskCanceledException => ErrorCategory.TimeoutError,
        HttpRequestException or global::System.Net.Sockets.SocketException => ErrorCategory.NetworkError,
        NullReferenceException or ArgumentException or InvalidOperationException => ErrorCategory.RuntimeError,
        UnauthorizedAccessException => ErrorCategory.SafetyBlock,
        TimeoutException => ErrorCategory.TimeoutError,
        _ => ErrorCategory.RuntimeError
    };

    public static ErrorCategory ClassifyFromMessage(string message)
    {
        if (message.Contains("CS") && message.Length < 20) return ErrorCategory.BuildError;
        if (message.Contains("timeout") || message.Contains("timed out")) return ErrorCategory.TimeoutError;
        if (message.Contains("budget") || message.Contains("limit")) return ErrorCategory.BudgetError;
        if (message.Contains("blocked") || message.Contains("safety")) return ErrorCategory.SafetyBlock;
        if (message.Contains("parse") || message.Contains("JSON") || message.Contains("malformed")) return ErrorCategory.ToolCallMalformed;
        return ErrorCategory.Unknown;
    }
}
