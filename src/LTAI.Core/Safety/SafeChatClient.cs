using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Safety;

/// <summary>
/// Decorator for <see cref="IChatClient"/> that intercepts responses and checks
/// them for safety concerns before returning to the caller.
/// 
/// Unlike <see cref="SafetyCoordinator"/> (which runs inside the MAF AIContextProvider
/// pipeline and cannot block output post-facto), this decorator wraps the LLM client
/// itself, enabling TRUE output blocking: if the response is unsafe, it is replaced
/// with a safe refusal message before the caller ever sees it.
/// </summary>
public sealed class SafeChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly IChatClient _safetyLlm;
    private readonly ILogger<SafeChatClient> _logger;
    private readonly AsyncLocal<bool> _isChecking = new();

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

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // For streaming, we buffer the full response then check
        var buffer = new System.Text.StringBuilder();
        await foreach (var update in _inner.GetStreamingResponseAsync(messages, options, ct).ConfigureAwait(false))
        {
            if (update.Text != null) buffer.Append(update.Text);
            yield return update;
        }

        // Check after streaming completes
        var fullText = buffer.ToString();
        if (string.IsNullOrEmpty(fullText)) yield break;

        var (safe, reason) = await CheckSafetyAsync(fullText, ct).ConfigureAwait(false);
        if (!safe)
        {
            _logger?.LogWarning("SafeChatClient blocked unsafe streaming output: {Reason}", reason);
        }
    }

    private async Task<(bool safe, string reason)> CheckSafetyAsync(string text, CancellationToken ct)
    {
        if (text.Length > 100_000)
            return (false, "Response exceeds 100k chars");

        if (_isChecking.Value)
            return (true, "");

        try
        {
            _isChecking.Value = true;
            var response = await _safetyLlm.GetResponseAsync([
                new ChatMessage(ChatRole.System, SafetySystemPrompt),
                new ChatMessage(ChatRole.User, text)
            ], cancellationToken: ct).ConfigureAwait(false);

            var verdict = response.Messages?.LastOrDefault()?.Text?.Trim() ?? "SAFE";
            return verdict.StartsWith("UNSAFE", StringComparison.OrdinalIgnoreCase)
                ? (false, verdict.Length > 7 ? verdict[7..].TrimStart(':', ' ') : "Blocked")
                : (true, "");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "SafeChatClient safety check failed");
            return (false, "Safety check unavailable — blocking by default (fail-closed)");
        }
        finally
        {
            _isChecking.Value = false;
        }
    }

    object? IChatClient.GetService(Type? serviceType, object? serviceKey) =>
        _inner.GetService(serviceType, serviceKey);

    void IDisposable.Dispose() => _inner.Dispose();
}
