using System;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Core.Safety;

/// <summary>
/// MAF AIContextProvider that checks agent input for safety BEFORE processing.
/// Uses a dedicated safety IChatClient (separate from the agent pipeline) to avoid recursion.
///
/// ⚠ IMPORTANT DESIGN NOTE:
///   - INPUT blocking: ProvideAIContextAsync replaces agent context with a system refusal
///     message. This blocks the agent FROM responding to unsafe prompts.
///   - OUTPUT checking: StoreAIContextAsync is AUDIT-ONLY — the response has already been
///     delivered to the user by the time this runs. For true output blocking, use
///     <see cref="SafeChatClient"/> which wraps IChatClient at a lower level.
///
/// ⚠ Recursion guard: _safeLock (SemaphoreSlim) prevents re-entrant safety checks.
/// Uses non-blocking Wait(0) — no ExecutionContext dependency, safe across
/// ConfigureAwait(false) boundaries.
///
/// <b>Consumers:</b> Registered in Agent/ServiceCollectionExtensions.cs as an
/// AIContextProvider in the MAF pipeline.
/// </summary>
public sealed class SafetyCoordinator : AIContextProvider, IDisposable
{
    private readonly IChatClient _llm;
    private readonly ILogger<SafetyCoordinator>? _logger;
    // SemaphoreSlim(1,1) with Wait(0) for non-blocking re-entrancy check.
    // More robust than AsyncLocal<bool> because it is not subject to
    // ExecutionContext flow issues across ConfigureAwait(false) boundaries.
    private readonly SemaphoreSlim _safeLock = new(1, 1);

    private readonly string _safetySystemPrompt;
    private readonly int _maxInputChars;

    public SafetyCoordinator(IChatClient llm, ILogger<SafetyCoordinator>? logger = null,
        string? safetyPrompt = null, int maxInputChars = 200_000)
        : base(null, null, null)
    {
        _llm = llm;
        _logger = logger;
        _safetySystemPrompt = safetyPrompt ?? SafetyPrompts.DefaultSystemPrompt;
        _maxInputChars = Math.Max(10_000, maxInputChars);
    }

    /// <summary>
    /// MAF pipeline hook: checks the latest user message for safety before processing.
    /// If unsafe, replaces the agent context with a system refusal message.
    /// Called by MAF kernel during agent invocation.
    /// </summary>
    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken ct = default)
    {
        var msgs = context.AIContext?.Messages;
        if (msgs == null) return context.AIContext!;

        var userMsg = msgs.LastOrDefault(m => m.Role == ChatRole.User);
        if (userMsg?.Text == null) return context.AIContext!;

        var (allowed, reason) = await CheckAsync(userMsg.Text, "input", ct).ConfigureAwait(false);
        if (!allowed)
        {
            _logger?.LogWarning("Safety blocked input: {Reason}", reason);
            // Return a modified context instructing the agent to reject
            return new AIContext
            {
                Messages =
                [
                    new ChatMessage(ChatRole.System,
                        $"The user's last message was blocked by safety filter. " +
                        $"Do NOT process it. Politely inform the user: \"I cannot process that " +
                        $"request because it was flagged by our safety system. Reason: {reason}\"."),
                ],
            };
        }
        return context.AIContext!;
    }

    public void Dispose() => _safeLock.Dispose();

    /// <summary>
    /// F14: Blocking-safe token. Set by <see cref="StoreAIContextAsync"/> when the
    /// output is flagged unsafe. Cleared by ChatAgent (via <see cref="ConsumeBlock"/>)
    /// before each new request so stale flags don't block unrelated responses.
    /// Thread-safe via Interlocked.
    /// </summary>
    private static int _outputBlocked;
    private static string? _outputBlockedReason;

    /// <summary>Get and clear the output-blocked flag. Returns reason or null.</summary>
    public static string? ConsumeBlock()
    {
        if (Interlocked.Exchange(ref _outputBlocked, 0) != 1)
            return null;
        var reason = Interlocked.Exchange(ref _outputBlockedReason, null);
        return reason;
    }

    /// <summary>
    /// MAF pipeline hook: checks generated output and sets a blocking flag
    /// that ChatAgent consumes after RunAsync/RunStreamingAsync completes.
    /// True blocking is handled by <see cref="SafeChatClient"/> at the IChatClient
    /// layer — this is defense-in-depth for the AIContextProvider layer.
    /// </summary>
    protected override async ValueTask StoreAIContextAsync(InvokedContext context, CancellationToken ct = default)
    {
        var response = context.ResponseMessages?.LastOrDefault();
        if (response?.Text == null) return;

        var (allowed, reason) = await CheckAsync(response.Text, "output", ct).ConfigureAwait(false);
        if (!allowed)
        {
            _logger?.LogWarning("Safety blocked output: {Reason}", reason);
            Interlocked.Exchange(ref _outputBlocked, 1);
            Interlocked.Exchange(ref _outputBlockedReason, reason);
        }
    }

    // 常见安全/简短指令直接跳过 LLM 审核
    private static readonly HashSet<string> SafePrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "你好", "hi", "hello", "早上好", "下午好", "晚上好", "再见", "谢谢", "感谢",
        "查看", "读取", "读", "打开", "列出", "搜索", "查找", "找", "显示",
        "llm", "deepseek", "help", "/", "clear", "cls",
    };


    /// <summary>轻量规则级安全预检（零 LLM 成本）。使用 SafetyRules 集中定义。</summary>
    private static bool IsSafeByRules(string text) => SafetyRules.IsSafeByRules(text);

    private async Task<(bool allow, string reason)> CheckAsync(string text, string direction, CancellationToken ct = default)
    {
        if (text.Length > _maxInputChars)
            return (false, $"Input exceeds {_maxInputChars / 1000}k chars");

        // 快速通道：常见安全短文本直接放行（无需 LLM 审核）
        if (text.Length <= 50)
        {
            var trimmed = text.TrimStart();
            if (SafePrefixes.Any(p => trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                _logger?.LogDebug("SafetyFastPath({Direction}): OK (safe prefix)", direction);
                return (true, "");
            }
        }

        // 规则级预检（零 LLM 成本）：短文本且通过规则检查 → 直接放行
        // 比走 LLM 审核快 10-50 倍，覆盖 90%+ 的日常消息
        // 增大阈值到 500 以覆盖更多日常消息，减少不必要的 LLM 安全审核
        if (text.Length <= 500 && IsSafeByRules(text))
        {
            _logger?.LogDebug("SafetyRulePath({Direction}): OK ({Len} chars, safe by rules)", direction, text.Length);
            return (true, "");
        }

        // Shared cache hit — reuse verdict from any recent identical check
        var cachedVerdict = VerdictCache.Get(text);
        if (cachedVerdict.HasValue)
        {
            _logger?.LogDebug("SafetyCached({Direction}): HIT for text len={Len}", direction, text.Length);
            return cachedVerdict.Value;
        }

        // Non-blocking try: if already inside a safety check, skip (safe pass-through).
        // SemaphoreSlim.Wait(0) is synchronous and thread-safe — no ExecutionContext dependency.
        if (!_safeLock.Wait(0))
        {
            _logger?.LogDebug("Safety recursion guard triggered");
            return (true, "");
        }

        try
        {
            var response = await _llm.GetResponseAsync([
                new ChatMessage(ChatRole.System, _safetySystemPrompt),
                new ChatMessage(ChatRole.User, text)
            ], cancellationToken: ct).ConfigureAwait(false);

            var verdict = response.Messages?.LastOrDefault()?.Text?.Trim() ?? "SAFE";
            _logger?.LogDebug("Safety verdict ({Direction}): {Verdict}", direction, verdict);

            const string unsafePrefix = "UNSAFE:";
            (bool allow, string reason) result;
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

            VerdictCache.Set(text, result.allow, result.reason);

            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Safety LLM unavailable for {Direction} — blocking (fail-closed)", direction);
            return (false, "Safety LLM unavailable — blocking by default (fail-closed)");
        }
        finally
        {
            _safeLock.Release();
        }
    }
}
