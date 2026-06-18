namespace LTAI.Core.Configuration;

/// <summary>
/// Unified environment variable configuration.
/// Static class for zero-DI call sites. Reads env var first, falls back to
/// <see cref="Overrides"/> set from appsettings.json during initialization.
/// All defaults match the previous hardcoded values in individual files.
/// </summary>
public static class EnvironmentConfig
{
    /// <summary>Config overrides loaded from appsettings.json:LTAI:Environment.</summary>
    public static EnvironmentOverrides? Overrides { get; set; }

    // ── Concurrency ──
    public static int ShellConcurrency => Overrides?.ShellConcurrency ?? ReadEnvInt("LTAI_SHELL_CONCURRENCY", 8);
    public static int WasmConcurrency => Overrides?.WasmConcurrency ?? ReadEnvInt("LTAI_WASM_CONCURRENCY", 6);
    public static int MoaConcurrency => Overrides?.MoaConcurrency ?? ReadEnvInt("LTAI_MOA_CONCURRENCY", 6);
    public static int WorkflowConcurrency => Overrides?.WorkflowConcurrency ?? ReadEnvInt("LTAI_WORKFLOW_CONCURRENCY", 6);
    public static int JobMaxConcurrent => Overrides?.JobMaxConcurrent ?? ReadEnvInt("LTAI_JOB_MAX_CONCURRENT", 10);
    public static int SearchMaxDop => Overrides?.SearchMaxDop ?? ReadEnvInt("LTAI_SEARCH_MAX_DOP", Math.Min(Environment.ProcessorCount, 4));
    public static int IssueDetectorMaxDop => Overrides?.IssueDetectorMaxDop ?? ReadEnvInt("LTAI_ISSUE_DETECTOR_MAX_DOP", 4);
    public static int TaskQueueMax => Overrides?.TaskQueueMax ?? ReadEnvInt("LTAI_TASK_QUEUE_MAX", -1);

    // ── Timeout ──
    public static int ShellTimeoutSec => Overrides?.ShellTimeoutSec ?? ReadEnvInt("LTAI_SHELL_TIMEOUT_SEC", 30);
    public static int WasmTimeoutSec => Overrides?.WasmTimeoutSec ?? ReadEnvInt("LTAI_WASM_TIMEOUT_SEC", 60);
    public static int ScriptTimeoutSec => Overrides?.ScriptTimeoutSec ?? ReadEnvInt("LTAI_SCRIPT_TIMEOUT_SEC", 60);
    public static int JobProcessTimeoutSec => Overrides?.JobProcessTimeoutSec ?? ReadEnvInt("LTAI_JOB_PROCESS_TIMEOUT_SEC", 300);
    public static int RegexTimeoutMs => Overrides?.RegexTimeoutMs ?? ReadEnvInt("LTAI_REGEX_TIMEOUT_MS", 1000);
    public static int SqliteBusyMs => Overrides?.SqliteBusyMs ?? ReadEnvInt("LTAI_SQLITE_BUSY_MS", 5000);
    public static string RetryBackoffSec => Overrides?.RetryBackoffSec ?? Environment.GetEnvironmentVariable("LTAI_RETRY_BACKOFF_SEC") ?? "1,2,4,8,16";

    // ── Resource limits ──
    public static int ToolMaxOutputBytes => Overrides?.ToolMaxOutputBytes ?? ReadEnvInt("LTAI_TOOL_MAX_OUTPUT_BYTES", 102400);
    public static int JobMaxOutputChars => Overrides?.JobMaxOutputChars ?? ReadEnvInt("LTAI_JOB_MAX_OUTPUT_CHARS", 100000);
    public static int JobExpirationSec => Overrides?.JobExpirationSec ?? ReadEnvInt("LTAI_JOB_EXPIRATION_SEC", 60);
    public static int SqliteMmapMb => Overrides?.SqliteMmapMb ?? ReadEnvInt("LTAI_SQLITE_MMAP_MB", 256);
    public static int WasmModuleCacheMax => Overrides?.WasmModuleCacheMax ?? ReadEnvInt("LTAI_WASM_MODULE_CACHE_MAX", 32);
    public static int HttpMaxConn => Overrides?.HttpMaxConn ?? ReadEnvInt("LTAI_HTTP_MAX_CONN", 6);
    public static int HttpPoolLifetimeMin => Overrides?.HttpPoolLifetimeMin ?? ReadEnvInt("LTAI_HTTP_POOL_LIFETIME_MIN", 10);
    public static int WatcherBuffer => Overrides?.WatcherBuffer ?? ReadEnvInt("LTAI_WATCHER_BUFFER", 65536);

    // ── Cache & TTL ──
    public static int LlmCacheTtlMin => Overrides?.LlmCacheTtlMin ?? ReadEnvInt("LTAI_LLM_CACHE_TTL_MIN", 5);
    public static int CompressionMaxAgeDays => Overrides?.CompressionMaxAgeDays ?? ReadEnvInt("LTAI_COMPRESSION_MAX_AGE_DAYS", 30);
    public static int CgCacheSize => Overrides?.CgCacheSize ?? ReadEnvInt("LTAI_CG_CACHE_SIZE", 100);
    public static int CgCacheTtlSec => Overrides?.CgCacheTtlSec ?? ReadEnvInt("LTAI_CG_CACHE_TTL_SEC", 30);
    public static int MemoryConsolidationMinutes => Overrides?.MemoryConsolidationMinutes ?? ReadEnvInt("LTAI_MEMORY_CONSOLIDATION_MINUTES", 30);
    public static int MemoryRefineryMinutes => Overrides?.MemoryRefineryMinutes ?? ReadEnvInt("LTAI_MEMORY_REFINERY_MINUTES", 15);
    public static int ReachIndexMaxNodes => Overrides?.ReachIndexMaxNodes ?? ReadEnvInt("LTAI_REACH_INDEX_MAX_NODES", -1);
    public static int ReachIndexMaxEdges => Overrides?.ReachIndexMaxEdges ?? ReadEnvInt("LTAI_REACH_INDEX_MAX_EDGES", -1);
    public static int RateLimitCleanupMin => Overrides?.RateLimitCleanupMin ?? ReadEnvInt("LTAI_RATE_LIMIT_CLEANUP_MIN", 5);

    // ── Other ──
    public static int GreetingMaxLength => Overrides?.GreetingMaxLength ?? ReadEnvInt("LTAI_GREETING_MAX_LENGTH", 15);
    public static int OfficeMaxOutputChars => Overrides?.OfficeMaxOutputChars ?? ReadEnvInt("LTAI_OFFICE_MAX_OUTPUT_CHARS", 100000);
    public static int AuditMaxEntries => Overrides?.AuditMaxEntries ?? ReadEnvInt("LTAI_AUDIT_MAX_ENTRIES", 2000);
    public static int ReviewParallelism => Overrides?.ReviewParallelism ?? ReadEnvInt("LTAI_REVIEW_PARALLELISM", 4);
    public static int ProxyMaxConn => Overrides?.ProxyMaxConn ?? ReadEnvInt("LTAI_PROXY_MAX_CONN", 100);
    public static float EmbedBm25AvgDocLen => Overrides?.EmbedBm25AvgDocLen ?? (float.TryParse(Environment.GetEnvironmentVariable("LTAI_EMBED_BM25_AVG_DOC_LEN"), out var v) ? v : 20f);
    public static int InitTimeoutSec => Overrides?.InitTimeoutSec ?? ReadEnvInt("LTAI_INIT_TIMEOUT_SEC", 30);
    public static int RateLimitRequests => Overrides?.RateLimitRequests ?? ReadEnvInt("LTAI_RATE_LIMIT_REQUESTS", 60);
    public static int RateLimitWindowSec => Overrides?.RateLimitWindowSec ?? ReadEnvInt("LTAI_RATE_LIMIT_WINDOW_SEC", 60);
    public static int MemorySummarizeMaxPerCycle => Overrides?.MemorySummarizeMaxPerCycle ?? ReadEnvInt("LTAI_MEMORY_SUMMARIZE_MAX_PER_CYCLE", 10);
    public static int ContrastiveFeedbackMax => Overrides?.ContrastiveFeedbackMax ?? ReadEnvInt("LTAI_CONTRASTIVE_FEEDBACK_MAX", 5000);

    private static int ReadEnvInt(string key, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        return int.TryParse(raw, out var v) ? Math.Max(1, v) : fallback;
    }
}

/// <summary>
/// Override values loaded from appsettings.json:LTAI:Environment.
/// When set, these take precedence over environment variables.
/// </summary>
public sealed class EnvironmentOverrides
{
    // Concurrency
    public int? ShellConcurrency { get; init; }
    public int? WasmConcurrency { get; init; }
    public int? MoaConcurrency { get; init; }
    public int? WorkflowConcurrency { get; init; }
    public int? JobMaxConcurrent { get; init; }
    public int? SearchMaxDop { get; init; }
    public int? IssueDetectorMaxDop { get; init; }
    public int? TaskQueueMax { get; init; }

    // Timeout
    public int? ShellTimeoutSec { get; init; }
    public int? WasmTimeoutSec { get; init; }
    public int? ScriptTimeoutSec { get; init; }
    public int? JobProcessTimeoutSec { get; init; }
    public int? RegexTimeoutMs { get; init; }
    public int? SqliteBusyMs { get; init; }
    public string? RetryBackoffSec { get; init; }

    // Resource
    public int? ToolMaxOutputBytes { get; init; }
    public int? JobMaxOutputChars { get; init; }
    public int? JobExpirationSec { get; init; }
    public int? SqliteMmapMb { get; init; }
    public int? WasmModuleCacheMax { get; init; }
    public int? HttpMaxConn { get; init; }
    public int? HttpPoolLifetimeMin { get; init; }
    public int? WatcherBuffer { get; init; }

    // Cache
    public int? LlmCacheTtlMin { get; init; }
    public int? CompressionMaxAgeDays { get; init; }
    public int? CgCacheSize { get; init; }
    public int? CgCacheTtlSec { get; init; }
    public int? MemoryConsolidationMinutes { get; init; }
    public int? MemoryRefineryMinutes { get; init; }
    public int? ReachIndexMaxNodes { get; init; }
    public int? ReachIndexMaxEdges { get; init; }
    public int? RateLimitCleanupMin { get; init; }

    // Other
    public int? GreetingMaxLength { get; init; }
    public int? OfficeMaxOutputChars { get; init; }
    public int? AuditMaxEntries { get; init; }
    public int? ReviewParallelism { get; init; }
    public int? ProxyMaxConn { get; init; }
    public float? EmbedBm25AvgDocLen { get; init; }
    public int? InitTimeoutSec { get; init; }
    public int? RateLimitRequests { get; init; }
    public int? RateLimitWindowSec { get; init; }
    public int? MemorySummarizeMaxPerCycle { get; init; }
    public int? ContrastiveFeedbackMax { get; init; }
}
