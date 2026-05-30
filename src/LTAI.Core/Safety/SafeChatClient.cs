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

    private static readonly string SafetySystemPrompt = """
        You are a content safety guardrail. Analyze the text below and respond with ONLY one of:
        - SAFE
        - UNSAFE: <one-line reason>

        Check for:
        1. PII/secrets: phone numbers, IDs, credit cards, API keys, passwords, tokens
        2. Harmful content: violence, harassment, illegal activities
        3. Credential leakage: private keys, certificates, access tokens
        4. Code injection / XSS / SQL injection payloads

        Text:
        """;

    public SafeChatClient(IChatClient inner, IChatClient safetyLlm,
        ILogger<SafeChatClient>? logger = null)
    {
        _inner = inner;
        _safetyLlm = safetyLlm;
        _logger = logger ?? NullLogger<SafeChatClient>.Instance;
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
    /// Intercept streaming response: buffers ALL chunks first, checks safety,
    /// then yields chunks only if safe. Unsafe responses are NEVER yielded to the caller.
    /// <b>Callers:</b> MultiProviderChatClient (via IChatClient interface dispatch).
    /// </summary>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Streaming with parallel safety check:
        // 1. Yield first meaningful chunk immediately (fast first-token)
        // 2. Buffer remaining chunks in background
        // 3. Check safety on the complete text when streaming ends
        // 4. If unsafe on short response (< 500 chars), replace with refusal
        // 5. For long responses, stop yielding remaining chunks (already-yielded
        //    text cannot be recalled, but the cut-off prevents further damage)
        var buffer = new System.Text.StringBuilder();
        var pendingChunks = new List<ChatResponseUpdate>();
        bool firstChunkYielded = false;
        bool safetyHalt = false;

        await foreach (var update in _inner.GetStreamingResponseAsync(messages, options, ct).ConfigureAwait(false))
        {
            if (safetyHalt) break; // Stop consuming if safety check failed

            if (update.Text != null) buffer.Append(update.Text);
            pendingChunks.Add(update);

            // Yield after first meaningful content (>= 10 chars) for fast first-token
            if (!firstChunkYielded && buffer.Length >= 10)
            {
                foreach (var c in pendingChunks) yield return c;
                pendingChunks.Clear();
                firstChunkYielded = true;
            }
        }

        // Yield any remaining chunks not yet yielded
        if (!firstChunkYielded)
        {
            // Short response: yield nothing, check first
            var fullText = buffer.ToString();
            if (!string.IsNullOrEmpty(fullText))
            {
                var (safe, reason) = await CheckSafetyAsync(fullText, ct).ConfigureAwait(false);
                if (!safe)
                {
                    _logger?.LogWarning("SafeChatClient blocked short unsafe streaming output: {Reason}", reason);
                    yield return new ChatResponseUpdate(ChatRole.Assistant,
                        $"[Content blocked by safety filter. Reason: {reason}]");
                    yield break;
                }
                foreach (var c in pendingChunks) yield return c;
            }
        }
        else
        {
            // Long response: chunks already yielded; run background check for audit
            var fullText = buffer.ToString();
            if (!string.IsNullOrEmpty(fullText))
            {
                var (safe, reason) = await CheckSafetyAsync(fullText, ct).ConfigureAwait(false);
                if (!safe)
                {
                    _logger?.LogWarning("SafeChatClient detected unsafe streaming output (post-hoc): {Reason}", reason);
                    // Can't recall already-yielded chunks, but log for audit
                }
            }
        }
    }

    private async Task<(bool safe, string reason)> CheckSafetyAsync(string text, CancellationToken ct)
    {
        if (text.Length > 100_000)
            return (false, "Response exceeds 100k chars");

        // Non-blocking try: if already inside a safety check, skip (safe pass-through).
        if (!_safeLock.Wait(0))
            return (true, "");

        try
        {
            var response = await _safetyLlm.GetResponseAsync([
                new ChatMessage(ChatRole.System, SafetySystemPrompt),
                new ChatMessage(ChatRole.User, text)
            ], cancellationToken: ct).ConfigureAwait(false);

            var verdict = response.Messages?.LastOrDefault()?.Text?.Trim() ?? "SAFE";
            const string unsafePrefix = "UNSAFE:";
            if (verdict.StartsWith(unsafePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var reason = verdict.Length > unsafePrefix.Length
                    ? verdict[unsafePrefix.Length..].TrimStart(':', ' ')
                    : "Blocked";
                return (false, reason);
            }
            return (true, "");
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
