using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Safety;

/// <summary>
/// Decorator for <see cref="IChatClient"/> that intercepts responses and checks
/// them for safety concerns BEFORE returning to the caller.
/// 
/// Unlike <see cref="SafetyCoordinator"/> (which runs inside the MAF AIContextProvider
/// pipeline and only audits output post-delivery), this decorator wraps the LLM client
/// itself, enabling TRUE output blocking: unsafe responses are replaced with a safe
/// refusal message before the caller sees them.
///
/// <b>Consumers:</b> Wrapped around inner IChatClient in MultiProviderChatClient.
///
/// ⚠ KNOWN ISSUE (resolved): verdict parsing now uses const prefix \"UNSAFE:\" with
/// StartsWith validation — no longer assumes fixed offset.
/// ⚠ KNOWN ISSUE (resolved): Replaced AsyncLocal&lt;bool&gt; with SemaphoreSlim(1,1) + Wait(0)
/// — thread-safe, no ExecutionContext dependency across async boundaries.
/// </summary>
public sealed class SafeChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly IChatClient _safetyLlm;
    private readonly ILogger<SafeChatClient> _logger;
    // SemaphoreSlim(1,1) with Wait(0) for non-blocking re-entrancy check.
    // Replaces AsyncLocal<bool> — not subject to ExecutionContext flow issues.
    private readonly SemaphoreSlim _safeLock = new(1, 1);
    private readonly int _ringBufferCheckIntervalMs;
    private readonly int _ringBufferMaxChars;

    private readonly string _safetySystemPrompt;

    public SafeChatClient(IChatClient inner, IChatClient safetyLlm,
        ILogger<SafeChatClient>? logger = null,
        int ringBufferCheckIntervalMs = 200,
        int ringBufferMaxChars = 200,
        string? safetyPrompt = null)
    {
        _inner = inner;
        _safetyLlm = safetyLlm;
        _logger = logger ?? NullLogger<SafeChatClient>.Instance;
        _ringBufferCheckIntervalMs = Math.Max(50, ringBufferCheckIntervalMs);
        _ringBufferMaxChars = Math.Max(50, ringBufferMaxChars);
        _safetySystemPrompt = safetyPrompt ?? SafetyPrompts.DefaultSystemPrompt;
    }

    /// <summary>
    /// Intercept non-streaming response: get response from inner LLM, check safety,
    /// and replace with refusal if unsafe.
    /// <b>Callers:</b> MultiProviderChatClient (via IChatClient interface dispatch).
    /// </summary>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken ct = default)
    {
        var response = await _inner.GetResponseAsync(messages, options, ct).ConfigureAwait(false);
        var text = response.Messages?.LastOrDefault()?.Text;
        if (string.IsNullOrEmpty(text)) return response;

        var (safe, reason) = await CheckSafetyAsync(text, ct).ConfigureAwait(false);
        if (!safe)
        {
            _logger?.LogWarning("SafeChatClient blocked unsafe output: {Reason}", reason);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                $"[Content blocked by safety filter. Reason: {reason}]"));
        }

        return response;
    }

    /// <summary>
    /// Intercept streaming response: yields chunks progressively while running
    /// rule-based safety pre-checks on a ring buffer. Unsafe content is blocked
    /// before reaching the caller (not post-hoc).
    /// <b>Callers:</b> MultiProviderChatClient (via IChatClient interface dispatch).
    /// </summary>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var buffer = new System.Text.StringBuilder();
        var pendingChunks = new List<ChatResponseUpdate>();
        var lastCheck = DateTime.UtcNow;
        var checkIntervalMs = _ringBufferCheckIntervalMs;
        var maxBufferBeforeCheck = _ringBufferMaxChars;
        bool safetyHalt = false;
        bool yieldedAny = false;

        await foreach (var update in _inner.GetStreamingResponseAsync(messages, options, ct).ConfigureAwait(false))
        {
            if (safetyHalt) break;

            if (update.Text != null)
            {
                buffer.Append(update.Text);
                pendingChunks.Add(update);

                // Run rule-based pre-check periodically or when buffer is full
                var elapsed = (DateTime.UtcNow - lastCheck).TotalMilliseconds;
                if (elapsed >= checkIntervalMs || buffer.Length >= maxBufferBeforeCheck)
                {
                    lastCheck = DateTime.UtcNow;
                    if (!IsSafeByRules(buffer.ToString()))
                    {
                        _logger?.LogWarning("SafeChatClient blocked unsafe streaming content at {Len} chars", buffer.Length);
                        yield return new ChatResponseUpdate(ChatRole.Assistant,
                            $"[Content blocked by safety filter]");
                        safetyHalt = true;
                        yield break;
                    }

                    // Yield buffered chunks after passing rule check
                    foreach (var c in pendingChunks)
                        yield return c;
                    pendingChunks.Clear();
                    yieldedAny = true;
                }
            }
            else
            {
                // Non-text updates (tool calls, etc) — pass through immediately
                yield return update;
            }
        }

        // Yield any remaining chunks after final rule check
        if (pendingChunks.Count > 0 && !safetyHalt)
        {
            var remainingText = buffer.ToString();
            if (!string.IsNullOrEmpty(remainingText))
            {
                if (!IsSafeByRules(remainingText))
                {
                    _logger?.LogWarning("SafeChatClient blocked unsafe tail content");
                    if (!yieldedAny)
                    {
                        yield return new ChatResponseUpdate(ChatRole.Assistant,
                            $"[Content blocked by safety filter]");
                    }
                    yield break;
                }
                foreach (var c in pendingChunks)
                    yield return c;
            }
        }

        // Full LLM safety check for the complete text (post-hoc audit)
        if (!safetyHalt && yieldedAny)
        {
            var fullText = buffer.ToString();
            if (!string.IsNullOrEmpty(fullText))
            {
                var (safe, reason) = await CheckSafetyAsync(fullText, ct).ConfigureAwait(false);
                if (!safe)
                {
                    _logger?.LogWarning("SafeChatClient detected unsafe streaming output (post-hoc): {Reason}", reason);
                }
            }
        }
    }

    // ═══════════════════════════════════════════
    //  规则级安全预检（零 LLM 成本）
    // ═══════════════════════════════════════════

    /// <summary>
    /// 轻量规则级安全检测，覆盖常见不安全模式。
    /// 命中任何规则 → 直接拦截，不调 safety LLM。
    /// 规则通过 → 短文本（≤200 字符）直接放行，长文本才调 LLM。
    /// </summary>
    private static bool IsSafeByRules(string text) => SafetyRules.IsSafeByRules(text);

    private async Task<(bool safe, string reason)> CheckSafetyAsync(string text, CancellationToken ct)
    {
        if (text.Length > 100_000)
            return (false, "Response exceeds 100k chars");

        // ── Rule-based pre-check (zero LLM cost) ──
        if (!IsSafeByRules(text))
        {
            _logger?.LogWarning("SafeChatClient blocked by rules: detected unsafe pattern in {Len} chars", text.Length);
            return (false, "Content blocked by safety rules (pattern match)");
        }

        // ── Short safe texts: fast path, no LLM needed ──
        // Most tool outputs (≤ 200 chars) pass the rule check and skip the safety LLM entirely.
        if (text.Length <= 200)
        {
            _logger?.LogDebug("SafeChatClient fast path: {Len} chars, safe by rules, no LLM", text.Length);
            return (true, "");
        }

        // ── Shared cache hit ──
        var cachedVerdict = VerdictCache.Get(text);
        if (cachedVerdict.HasValue)
        {
            _logger?.LogDebug("SafeChatClient cache HIT for text len={Len}", text.Length);
            return cachedVerdict.Value;
        }

        // ── LLM safety check for long/complex texts only ──
        // Non-blocking try: if already inside a safety check, skip (safe pass-through).
        if (!_safeLock.Wait(0))
            return (true, "");

        try
        {
            var response = await _safetyLlm.GetResponseAsync([
                new ChatMessage(ChatRole.System, _safetySystemPrompt),
                new ChatMessage(ChatRole.User, text)
            ], cancellationToken: ct).ConfigureAwait(false);

            var verdict = response.Messages?.LastOrDefault()?.Text?.Trim() ?? "SAFE";
            const string unsafePrefix = "UNSAFE:";
            (bool safe, string reason) result;
            if (verdict.StartsWith(unsafePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var reason = verdict.Length > unsafePrefix.Length
                    ? verdict[unsafePrefix.Length..].TrimStart(':', ' ')
                    : "Blocked";
                result = (false, reason);
            }
            else
            {
                result = (true, "");
            }

            VerdictCache.Set(text, result.safe, result.reason);

            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "SafeChatClient safety check failed");
            return (false, "Safety check unavailable — blocking by default (fail-closed)");
        }
        finally
        {
            _safeLock.Release();
        }
    }

    object? IChatClient.GetService(Type? serviceType, object? serviceKey) =>
        _inner.GetService(serviceType!, serviceKey);

    void IDisposable.Dispose() => _inner.Dispose();
}
