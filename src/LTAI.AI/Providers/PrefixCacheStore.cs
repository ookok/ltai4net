using System.Text;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.AI.Providers;

// ============================================================================
// Cache-First Loop — Three-Zone Prefix Architecture
// Inspired by Reasonix's pillar design for DeepSeek prefix cache optimization.
//
// DeepSeek prefix cache: exact byte-prefix match → ~10% cost for cached tokens.
// Most agents break this by rewriting/inserting timestamps each turn.
//
// Three invariants:
//   1. IMMUTABLE PREFIX — system + tool specs locked after first use
//   2. APPEND-ONLY LOG — assistant[] + tool[] monotonically appended
//   3. VOLATILE SCRATCH — R1 thought / plan state, distilled before entering log
// ============================================================================

/// <summary>Metadata for parallel-safe tool dispatch.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class ParallelSafeAttribute : Attribute
{
    public bool Value { get; }
    public ParallelSafeAttribute(bool value = true) => Value = value;
}

/// <summary>
/// Three-zone prefix cache store for DeepSeek API optimization.
///
/// Zone 1 (IMMUTABLE): system prompt + tool definitions — computed once, never changed
/// Zone 2 (APPEND-ONLY): serialized conversation turns — only appended, never rewritten
/// Zone 3 (VOLATILE): transient thought/plan state — never sent upstream, distilled into Zone 2
/// </summary>
public sealed class PrefixCacheStore
{
    private readonly ILogger<PrefixCacheStore> _logger;

    // Zone 1: Immutable prefix
    private byte[] _immutablePrefixBytes = Array.Empty<byte>();
    private string _immutableHash = "";
    private bool _prefixLocked;
    private readonly object _prefixLock = new();

    // Zone 2: Append-only log
    private readonly List<ChatMessage> _log = new();
    private byte[] _logBytes = Array.Empty<byte>();
    private readonly object _logLock = new();

    // Zone 3: Volatile scratch (never serialized, R1 thought space)
    private readonly ConcurrentDictionary<string, object> _scratch = new();
    private readonly StringBuilder _scratchThought = new();

    // Stats
    private int _turnCount;
    private int _cacheHits;
    private int _cacheMisses;
    private long _estimatedSavingsTokens;

    public int TurnCount => _turnCount;
    public int CacheHits => _cacheHits;
    public int CacheMisses => _cacheMisses;
    public long EstimatedSavingsTokens => _estimatedSavingsTokens;
    public bool PrefixLocked => _prefixLocked;
    public string ImmutableHash => _immutableHash;

    public PrefixCacheStore(ILogger<PrefixCacheStore>? logger = null)
    {
        _logger = logger ?? NullLogger<PrefixCacheStore>.Instance;
    }

    // ════════════════════════════════════════════════════════════════
    // Zone 1: Immutable Prefix — lock once, never change
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Lock the immutable prefix. After this call, system prompt and tools
    /// MUST NOT change for the session. Any change requires a new session.
    /// </summary>
    public void LockPrefix(string systemPrompt, string toolsJson)
    {
        lock (_prefixLock)
        {
            if (_prefixLocked)
            {
                _logger.LogWarning("PrefixCache: prefix already locked, ignoring re-lock attempt");
                return;
            }

            var prefix = $"{systemPrompt}\n{toolsJson}";
            _immutablePrefixBytes = Encoding.UTF8.GetBytes(prefix);
            _immutableHash = ComputeSha256Hex(prefix);
            _prefixLocked = true;

            _logger.LogInformation("PrefixCache: locked immutable prefix ({Bytes} bytes, hash={Hash})",
                _immutablePrefixBytes.Length, _immutableHash[..8]);
        }
    }

    /// <summary>
    /// Returns the full cacheable prefix: immutable bytes + append-only log bytes.
    /// This byte sequence, when sent as the start of the next API request,
    /// will hit DeepSeek's prefix cache for the entire immutable + log portion.
    /// </summary>
    public byte[] GetCacheablePrefix()
    {
        lock (_prefixLock)
        lock (_logLock)
        {
            var totalLen = _immutablePrefixBytes.Length + _logBytes.Length;
            var result = new byte[totalLen];
            if (_immutablePrefixBytes.Length > 0)
                Buffer.BlockCopy(_immutablePrefixBytes, 0, result, 0, _immutablePrefixBytes.Length);
            if (_logBytes.Length > 0)
                Buffer.BlockCopy(_logBytes, 0, result, _immutablePrefixBytes.Length, _logBytes.Length);
            return result;
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Zone 2: Append-Only Log
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Append a turn to the conversation log. Monotonically increasing —
    /// never modifies or removes previous entries. This preserves the
    /// byte-prefix for cache hits.
    /// </summary>
    public void AppendTurn(ChatMessage userMessage, ChatMessage assistantMessage)
    {
        lock (_logLock)
        {
            _log.Add(userMessage);
            var userBytes = SerializeMessage(userMessage);
            _logBytes = AppendBytes(_logBytes, userBytes);

            _log.Add(assistantMessage);
            var asstBytes = SerializeMessage(assistantMessage);
            _logBytes = AppendBytes(_logBytes, asstBytes);

            _turnCount++;
        }
    }

    /// <summary>
    /// Append tool calls and their results to the log.
    /// Tool results are appended after their corresponding calls to maintain order.
    /// </summary>
    public void AppendToolTurn(string toolCallId, string toolName, string toolArgs, string toolResult)
    {
        lock (_logLock)
        {
            var toolCallMsg = new ChatMessage(ChatRole.Assistant,
                new List<AIContent>
                {
                    new FunctionCallContent(toolCallId, toolName,
                        new Dictionary<string, object?> { ["args"] = toolArgs })
                });
            var toolResultMsg = new ChatMessage(ChatRole.Tool, toolResult)
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["tool_call_id"] = toolCallId
                }
            };

            _log.Add(toolCallMsg);
            _logBytes = AppendBytes(_logBytes, SerializeMessage(toolCallMsg));

            _log.Add(toolResultMsg);
            _logBytes = AppendBytes(_logBytes, SerializeMessage(toolResultMsg));
        }
    }

    /// <summary>
    /// Get the conversation log messages (for building full message lists).
    /// </summary>
    public IReadOnlyList<ChatMessage> GetLog()
    {
        lock (_logLock) return _log.ToList();
    }

    // ════════════════════════════════════════════════════════════════
    // Zone 3: Volatile Scratch — never serialized to upstream
    // ════════════════════════════════════════════════════════════════

    /// <summary>Write to volatile scratch (R1 thought, plan state).</summary>
    public void WriteScratch(string key, object value)
    {
        _scratch[key] = value;
    }

    /// <summary>Read from volatile scratch.</summary>
    public T? ReadScratch<T>(string key) where T : class
    {
        return _scratch.TryGetValue(key, out var val) ? val as T : null;
    }

    /// <summary>Append thought to scratch buffer (cleared each turn).</summary>
    public void AppendThought(string thought)
    {
        lock (_scratchThought)
        {
            _scratchThought.AppendLine(thought);
        }
    }

    /// <summary>Read and clear the scratch thought buffer.</summary>
    public string FlushThought()
    {
        lock (_scratchThought)
        {
            var t = _scratchThought.ToString();
            _scratchThought.Clear();
            return t;
        }
    }

    /// <summary>Clear all volatile scratch for a new turn.</summary>
    public void ClearScratch()
    {
        _scratch.Clear();
        lock (_scratchThought) _scratchThought.Clear();
    }

    // ════════════════════════════════════════════════════════════════
    // Message Building (Cache-Optimized)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Build the full message list for an API call, optimized for prefix caching.
    /// The immutable prefix (system + tools) comes first, followed by the
    /// append-only log. Volatile scratch is NEVER included.
    ///
    /// Estimated savings: ~90% of input tokens when prefix cache hits.
    /// </summary>
    public List<ChatMessage> BuildCacheOptimizedMessages(
        ChatMessage systemMessage,
        List<AITool> tools,
        ChatMessage? currentUserMessage)
    {
        // Estimate cache savings
        var prefixBytes = GetCacheablePrefix();
        if (prefixBytes.Length > 0)
        {
            var estimatedTokens = prefixBytes.Length / 3; // rough: ~3 bytes per token
            Interlocked.Add(ref _estimatedSavingsTokens, estimatedTokens);
            Interlocked.Increment(ref _cacheHits);
        }
        else
        {
            Interlocked.Increment(ref _cacheMisses);
        }

        var messages = new List<ChatMessage> { systemMessage };

        // Add conversation log (append-only, preserves byte-prefix)
        lock (_logLock)
        {
            messages.AddRange(_log);
        }

        // Add current user message last (this is the new, uncached suffix)
        if (currentUserMessage != null)
            messages.Add(currentUserMessage);

        return messages;
    }

    /// <summary>
    /// Reset the append-only log (for session reset only).
    /// WARNING: breaks prefix cache — use sparingly.
    /// </summary>
    public void ResetLog()
    {
        lock (_logLock)
        {
            _log.Clear();
            _logBytes = Array.Empty<byte>();
            _turnCount = 0;
        }
        _logger.LogInformation("PrefixCache: log reset — cache invalidated");
    }

    public string GetCacheStats()
    {
        var prefixLen = _immutablePrefixBytes.Length;
        var logLen = _logBytes.Length;
        var totalCacheable = prefixLen + logLen;
        var estTokens = totalCacheable / 3;
        return $"locked={_prefixLocked} turns={_turnCount} cacheable_bytes={totalCacheable} est_tokens={estTokens} hits={_cacheHits} misses={_cacheMisses} savings={_estimatedSavingsTokens}";
    }

    // ════════════════════════════════════════════════════════════════
    // Parallel Tool Dispatch
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Batch parallel-safe tools for concurrent execution.
    /// Reads the [ParallelSafe] attribute on tool functions to decide.
    /// Returns groups: each group can run in parallel, groups are sequential.
    /// </summary>
    public static List<List<AITool>> BatchParallelSafe(IReadOnlyList<AITool> tools)
    {
        var batches = new List<List<AITool>>();
        var currentBatch = new List<AITool>();

        foreach (var tool in tools)
        {
            var isParallelSafe = IsParallelSafe(tool);
            if (isParallelSafe)
            {
                currentBatch.Add(tool);
            }
            else
            {
                if (currentBatch.Count > 0)
                {
                    batches.Add(currentBatch);
                    currentBatch = new List<AITool>();
                }
                batches.Add(new List<AITool> { tool });
            }
        }

        if (currentBatch.Count > 0)
            batches.Add(currentBatch);

        return batches;
    }

    private static bool IsParallelSafe(AITool tool)
    {
        try
        {
            // Check for ParallelSafe attribute via reflection
            var method = tool.GetType().GetMethod("InvokeAsync")
                ?? tool.GetType().GetMethod("Invoke");
            var attr = method?.GetCustomAttributes(typeof(ParallelSafeAttribute), false)
                .FirstOrDefault() as ParallelSafeAttribute;
            return attr?.Value ?? false;
        }
        catch
        {
            return false; // default: not parallel-safe
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Internal Helpers
    // ════════════════════════════════════════════════════════════════

    private static byte[] SerializeMessage(ChatMessage msg)
    {
        // Deterministic serialization: role + text content
        // Avoids JSON serializer variability that breaks byte-prefix cache
        var sb = new StringBuilder();
        sb.Append(msg.Role.Value);
        sb.Append(':');
        if (!string.IsNullOrEmpty(msg.Text))
            sb.Append(msg.Text);
        sb.Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] AppendBytes(byte[] existing, byte[] addition)
    {
        var result = new byte[existing.Length + addition.Length];
        if (existing.Length > 0)
            Buffer.BlockCopy(existing, 0, result, 0, existing.Length);
        Buffer.BlockCopy(addition, 0, result, existing.Length, addition.Length);
        return result;
    }

    private static string ComputeSha256Hex(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hash);
    }

    // Legacy API compatibility
    public void SetSystemPrompt(string systemPrompt)
    {
        // No-op: use LockPrefix instead
    }

    public void SetToolDefinitions(string toolsJson)
    {
        // No-op: use LockPrefix instead
    }

    public void AppendTurn(string userMessage, string assistantResponse)
    {
        AppendTurn(
            new ChatMessage(ChatRole.User, userMessage),
            new ChatMessage(ChatRole.Assistant, assistantResponse));
    }

    public string GetConversationLog()
    {
        lock (_logLock) return string.Join("\n", _log.Select(m => m.Text ?? ""));
    }

    public void ResetVolatileLog()
    {
        ResetLog();
    }

    public List<ChatMessage> BuildMessages(
        ChatMessage systemMessage,
        List<AITool> tools,
        List<ChatMessage>? conversationHistory)
    {
        return BuildCacheOptimizedMessages(systemMessage, tools, null);
    }
}
