using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.MAF;

// ============================================================================
// Cache-First Context Builder — adapted from DeepSeek-Reasonix Pillar 1.
// Manages context in three regions to maximize DeepSeek prefix-cache hit rate:
//
//   ┌─ IMMUTABLE PREFIX ─────┐ ← Computed once, never changed
//   │ system + tool_specs     │   Cache hit candidate
//   ├─ APPEND-ONLY LOG ──────┤ ← Monotonically growing
//   │ [assistant₁][tool₁]...  │   Preserves prior-turn prefixes
//   ├─ VOLATILE SCRATCH ─────┤ ← Reset every turn
//   │ current thought / plan  │   Not sent upstream
//   └─────────────────────────┘
//
// Three invariants:
// 1. Prefix computed once, hashed, locked — never mutated
// 2. Log is append-only — serialized in order, never rewrites existing entries
// 3. Scratch is distilled before entering Log
// ============================================================================

/// <summary>
/// Represents one of the three context regions.
/// </summary>
public enum ContextRegion { ImmutablePrefix, AppendOnlyLog, VolatileScratch }

/// <summary>
/// A single entry in the append-only log.
/// </summary>
public sealed record LogEntry
{
    public string Role { get; init; } = ""; // "assistant" or "tool"
    public string Content { get; init; } = "";
    public string? ToolName { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public int TurnNumber { get; init; }
}

/// <summary>
/// Manages the three-region context model for DeepSeek prefix-cache optimization.
/// Thread-safe for append operations.
/// </summary>
public sealed class CacheFirstContextBuilder
{
    private readonly ILogger<CacheFirstContextBuilder> _logger;
    private readonly object _lock = new();

    // Immutable prefix — computed once
    private string _immutablePrefix = "";
    private string _prefixHash = "";
    private bool _prefixLocked;

    // Append-only log
    private readonly List<LogEntry> _log = new();
    private int _turnNumber;

    // Volatile scratch — reset each turn
    private string _volatileScratch = "";

    // Metrics
    private long _estimatedPrefixBytes;
    private long _totalLogBytes;

    public string PrefixHash => _prefixHash;
    public bool PrefixLocked => _prefixLocked;
    public int LogEntryCount { get { lock (_lock) return _log.Count; } }
    public int TurnNumber => _turnNumber;
    public long EstimatedPrefixBytes => _estimatedPrefixBytes;
    public long TotalLogBytes => _totalLogBytes;

    public CacheFirstContextBuilder(ILogger<CacheFirstContextBuilder>? logger = null)
    {
        _logger = logger ?? NullLogger<CacheFirstContextBuilder>.Instance;
    }

    /// <summary>
    /// Set and lock the immutable prefix. Called once at session start.
    /// After this call, the prefix cannot be changed.
    /// </summary>
    public void LockPrefix(string systemPrompt, string toolSpecs)
    {
        if (_prefixLocked)
        {
            _logger.LogWarning("CacheFirstContext: Prefix already locked — ignoring duplicate LockPrefix call");
            return;
        }

        lock (_lock)
        {
            _immutablePrefix = systemPrompt + "\n\n" + toolSpecs;
            _estimatedPrefixBytes = Encoding.UTF8.GetByteCount(_immutablePrefix);

            // Compute SHA256 hash for cache-key verification
            _prefixHash = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(_immutablePrefix)));

            _prefixLocked = true;

            _logger.LogInformation(
                "CacheFirstContext: Prefix locked. Hash={Hash}, Size={Size} bytes",
                _prefixHash[..12], _estimatedPrefixBytes);
        }
    }

    /// <summary>
    /// Append an entry to the append-only log. Thread-safe.
    /// </summary>
    public void AppendToLog(string role, string content, string? toolName = null)
    {
        if (!_prefixLocked)
        {
            _logger.LogWarning("CacheFirstContext: Append before prefix locked — auto-locking with empty prefix");
            LockPrefix("", "");
        }

        var entry = new LogEntry
        {
            Role = role,
            Content = content,
            ToolName = toolName,
            TurnNumber = _turnNumber
        };

        lock (_lock)
        {
            _log.Add(entry);
            _totalLogBytes += Encoding.UTF8.GetByteCount(content);
        }
    }

    /// <summary>
    /// Set the volatile scratch content for the current turn.
    /// </summary>
    public void SetScratch(string content)
    {
        _volatileScratch = content;
    }

    /// <summary>
    /// Get the full context for the current request.
    /// Returns: immutable prefix + append-only log (volatile scratch is NOT included).
    /// </summary>
    public string BuildRequestContext()
    {
        if (!_prefixLocked)
        {
            _logger.LogWarning("CacheFirstContext: BuildRequestContext before prefix locked");
            return _volatileScratch;
        }

        var sb = new StringBuilder();
        sb.Append(_immutablePrefix);

        List<LogEntry> snapshot;
        lock (_lock)
        {
            snapshot = _log.ToList();
        }

        foreach (var entry in snapshot)
        {
            sb.Append("\n\n");
            sb.Append(entry.Role switch
            {
                "assistant" => $"[assistant_turn{entry.TurnNumber}]",
                "tool" => $"[tool_result:{entry.ToolName ?? "unknown"}]",
                _ => $"[{entry.Role}]"
            });
            sb.Append('\n');
            sb.Append(entry.Content);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Build a message array suitable for sending to the model.
    /// Preserves the exact byte prefix for cache hits.
    /// </summary>
    public List<(string Role, string Content)> BuildMessages(string currentScratch)
    {
        var messages = new List<(string Role, string Content)>();

        // Immutable prefix goes into the system message
        if (_prefixLocked)
        {
            messages.Add(("system", _immutablePrefix));
        }

        // Append-only log as assistant/tool turns
        List<LogEntry> snapshot;
        lock (_lock)
        {
            snapshot = _log.ToList();
        }

        foreach (var entry in snapshot)
        {
            messages.Add((entry.Role, entry.Content));
        }

        // Current scratch as the user message
        if (!string.IsNullOrEmpty(currentScratch))
        {
            messages.Add(("user", currentScratch));
        }

        return messages;
    }

    /// <summary>
    /// Advance to the next turn. Scratch is NOT automatically promoted to log —
    /// call DistillScratchToLog() explicitly when the turn's result should be persisted.
    /// </summary>
    public void AdvanceTurn()
    {
        lock (_lock)
        {
            _turnNumber++;
        }
        _volatileScratch = "";
    }

    /// <summary>
    /// Distill the current scratch content into the append-only log.
    /// This is the "distillation gate" — only valuable output enters the log.
    /// </summary>
    public void DistillScratchToLog(string role, string? toolName = null)
    {
        if (string.IsNullOrEmpty(_volatileScratch))
            return;

        AppendToLog(role, _volatileScratch, toolName);
        _volatileScratch = "";
    }

    /// <summary>
    /// Estimate the cache hit rate based on prefix stability.
    /// </summary>
    public double EstimateCacheHitRate()
    {
        if (!_prefixLocked || _log.Count == 0)
            return 0;

        // Simplified model: prefix is always cache-hit, log entries depend on stability
        var prefixRatio = (double)_estimatedPrefixBytes / (_estimatedPrefixBytes + _totalLogBytes);
        var logStability = Math.Min(1.0, 10.0 / Math.Max(1, _log.Count));

        return prefixRatio + (1 - prefixRatio) * logStability;
    }

    /// <summary>
    /// Get a diagnostic summary of the context state.
    /// </summary>
    public Dictionary<string, object> GetDiagnostics()
    {
        return new()
        {
            ["prefix_locked"] = _prefixLocked,
            ["prefix_hash"] = _prefixHash.Length > 0 ? _prefixHash[..12] : "",
            ["prefix_bytes"] = _estimatedPrefixBytes,
            ["log_entries"] = _log.Count,
            ["log_bytes"] = _totalLogBytes,
            ["turn_number"] = _turnNumber,
            ["scratch_bytes"] = Encoding.UTF8.GetByteCount(_volatileScratch),
            ["estimated_hit_rate"] = EstimateCacheHitRate()
        };
    }
}
