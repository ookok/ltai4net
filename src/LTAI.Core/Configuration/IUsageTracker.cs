namespace LTAI.Core.Configuration;

/// <summary>
/// Interface for token/cost tracking. Inject via DI for per-scope tracking,
/// or use static <see cref="UsageTracker.Current"/> for existing callers.
/// Implementations must be thread-safe.
/// </summary>
public interface IUsageTracker
{
    /// <summary>Record token usage from an API call.</summary>
    void Record(int prompt, int completion, string model = "");
    /// <summary>Record token usage with cache breakdown (三档计价).</summary>
    void RecordWithCache(int prompt, int completion, int cacheHit, int cacheMiss, string model);
    /// <summary>Record a response cache hit.</summary>
    void RecordCacheHit();
    /// <summary>Record a response cache miss.</summary>
    void RecordCacheMiss();
    /// <summary>Total prompt tokens.</summary>
    long PromptTokens { get; }
    /// <summary>Total completion tokens.</summary>
    long CompletionTokens { get; }
    /// <summary>Total requests.</summary>
    long Requests { get; }
    /// <summary>Estimated cost in ¥.</summary>
    decimal EstimatedCost { get; }
    /// <summary>Cache hit count.</summary>
    long CacheHits { get; }
    /// <summary>Cache miss count.</summary>
    long CacheMisses { get; }
    /// <summary>Cache hit rate (0-100%).</summary>
    double CacheHitRate { get; }
    /// <summary>Context usage ratio 0.0-1.0.</summary>
    double ContextRatio(int contextWindowOverride = 0);
    /// <summary>Context usage text (e.g. "12,345/64,000 (19.3%)").</summary>
    string ContextText(int contextWindowOverride = 0);
    /// <summary>One-line summary of session stats.</summary>
    string Summary();
    /// <summary>Cost display string (¥ prefix).</summary>
    string CostDisplay { get; }
    /// <summary>Active model name.</summary>
    string ActiveModel { get; }
    /// <summary>Account balance display.</summary>
    string BalanceDisplay { get; }
    /// <summary>Fetch balance from provider API (best-effort).</summary>
    Task FetchBalanceAsync(string defaultProvider, string? apiKey = null);
    /// <summary>Set active model name.</summary>
    void SetActiveModel(string model);
    /// <summary>Set context window size.</summary>
    void SetContextWindowSize(int size);
    /// <summary>Cache hit tokens (from API).</summary>
    long CacheHitTokens { get; }
    /// <summary>Cache miss tokens (from API).</summary>
    long CacheMissTokens { get; }
    /// <summary>Tool call count.</summary>
    long ToolCalls { get; }
    /// <summary>Cache saved amount display.</summary>
    string CacheSavedDisplay { get; }
    /// <summary>Record streaming metrics for t/s calculation.</summary>
    void RecordStreamingMetrics(long completionTokens, long elapsedMs);
    /// <summary>Current tokens-per-second (null if insufficient data).</summary>
    double? CurrentTps { get; }
    /// <summary>Tool call count.</summary>
    string TpsDisplay { get; }
    /// <summary>Set currently executing tool name (for TUI animation).</summary>
    void SetActiveTool(string toolName);
    /// <summary>Currently executing tool name, empty if none.</summary>
    string CurrentTool { get; }

    /// <summary>
    /// Lightweight turn outcome marker — zero IO, in-memory only.
    /// When a turn fails or produces low-quality output, call this with reason.
    /// These markers serve as MOSS-style batch input for future self-evolution.
    /// </summary>
    void RecordTurnOutcome(bool success, string? reason = null);
    /// <summary>Cumulative count of failed turns.</summary>
    long FailedTurns { get; }
    /// <summary>Reason from the most recently recorded failure.</summary>
    string? LastFailureReason { get; }
}
