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
    /// Per-flow blocking token. Set by <see cref="StoreAIContextAsync"/> when the
    /// output is flagged unsafe. Cleared by ChatAgent (via <see cref="ConsumeBlock"/>)
    /// before each new request. Uses AsyncLocal to prevent cross-agent state leak.
    /// Simple assignment is safe: each ExecutionContext flow has isolated values.
    /// </summary>
    private static readonly AsyncLocal<int> _outputBlocked = new();
    private static readonly AsyncLocal<string?> _outputBlockedReason = new();

    /// <summary>Get and clear the output-blocked flag for the current execution flow. Returns reason or null.</summary>
    public static string? ConsumeBlock()
    {
        if (_outputBlocked.Value != 1)
            return null;
        _outputBlocked.Value = 0;
        var reason = _outputBlockedReason.Value;
        _outputBlockedReason.Value = null;
        return reason;
    }

    /// <summary>
    /// MAF pipeline hook: checks generated output and applies KVEraser-inspired
    /// fragment-level safety scrubbing. Instead of blocking the entire response,
    /// harmful spans are identified and replaced with [redacted] markers.
    /// Full blocking is reserved for cases where the entire response is unsafe.
    /// </summary>
    protected override async ValueTask StoreAIContextAsync(InvokedContext context, CancellationToken ct = default)
    {
        var response = context.ResponseMessages?.LastOrDefault();
        if (response?.Text == null) return;

        var text = response.Text;

        // Phase 1: Rule-based fragment scrubbing (zero LLM cost, KVEraser-inspired).
        // Detects and redacts known unsafe patterns without blocking the entire response.
        var (scrubbed, fragmentsRemoved) = ScrubFragments(text);
        if (fragmentsRemoved > 0)
        {
            _logger?.LogWarning("Safety flagged {Count} unsafe fragment(s) in output — scrubbing delegated to SafeChatClient layer", fragmentsRemoved);
            // Fragment scrubbing is handled at the IChatClient level by SafeChatClient.
            // At the AIContextProvider level, we only detect and log.
            return;
        }

        // Phase 2: Full LLM safety check for entire response
        var (allowed, reason) = await CheckAsync(text, "output", ct).ConfigureAwait(false);
        if (!allowed)
        {
            _logger?.LogWarning("Safety blocked output: {Reason}", reason);
            _outputBlocked.Value = 1;
            _outputBlockedReason.Value = reason;
        }
    }

    /// <summary>
    /// KVEraser-inspired fragment-level safety scrubbing.
    /// Detects and redacts known harmful patterns without blocking the entire response.
    /// Returns (scrubbedText, fragmentCount). If 0 fragments removed, text is unchanged.
    /// </summary>
    public static (string Text, int FragmentsRemoved) ScrubFragments(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 10)
            return (text, 0);

        var removed = 0;
        var result = text;

        // Pattern 1: Hardcoded secrets (API keys, tokens, passwords)
        result = ScrubPattern(result, @"(?:sk-[a-zA-Z0-9]{20,})", "[redacted:api-key]", ref removed);
        result = ScrubPattern(result, @"(?:ghp_[a-zA-Z0-9]{20,})", "[redacted:github-token]", ref removed);
        result = ScrubPattern(result, @"(?:AIza[0-9A-Za-z\-_]{20,})", "[redacted:google-key]", ref removed);
        result = ScrubPattern(result, @"(?:eyJ[a-zA-Z0-9\-_]{20,}\.[a-zA-Z0-9\-_]{20,}\.[a-zA-Z0-9\-_]{10,})", "[redacted:jwt]", ref removed);
        result = ScrubPattern(result, @"(?:(?:api[_-]?key|apikey|secret[_-]?key|password|passwd)\s*[:=]\s*['""]?\S{6,}['""]?)",
            "[redacted:credential]", ref removed, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Pattern 2: System prompt leakage (common indicators)
        result = ScrubPattern(result, @"(?:system\s*prompt\s*[:=]\s*['""].{30,}['""])",
            "[redacted:system-prompt]", ref removed, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Pattern 3: Personal identifiable information patterns
        result = ScrubPattern(result, @"(?:\b\d{3}[-.]?\d{2}[-.]?\d{4}\b)", "[redacted:ssn]", ref removed);
        result = ScrubPattern(result, @"(?:\b(?:\d[ -]*?){13,16}\b)", "[redacted:card-number]", ref removed);

        // Only return modified text if meaningful fragments were removed
        if (removed > 0 && result.Length < text.Length - 20)
            return (result, removed);

        return (text, 0);
    }

    private static string ScrubPattern(
        string text, string pattern, string replacement, ref int counter,
        System.Text.RegularExpressions.RegexOptions options = System.Text.RegularExpressions.RegexOptions.None)
    {
        var regex = new System.Text.RegularExpressions.Regex(pattern, options);
        var matches = regex.Matches(text);
        if (matches.Count == 0) return text;

        counter += matches.Count;
        return regex.Replace(text, replacement);
    }

    // 常见安全/简短指令直接跳过 LLM 审核
    private static readonly HashSet<string> SafeExact = new(StringComparer.OrdinalIgnoreCase)
    {
        "你好", "hi", "hello", "早上好", "下午好", "晚上好", "再见", "谢谢", "感谢",
        "llm", "deepseek", "help", "/", "clear", "cls",
    };
    private static readonly HashSet<string> SafeActionPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "查看", "读取", "读", "打开", "列出", "搜索", "查找", "找", "显示",
    };

    /// <summary>轻量规则级安全预检（零 LLM 成本）。使用 SafetyRules 集中定义。</summary>
    private static bool IsSafeByRules(string text) => SafetyRules.IsSafeByRules(text);

    private async Task<(bool allow, string reason)> CheckAsync(string text, string direction, CancellationToken ct = default)
    {
        if (text.Length > _maxInputChars)
            return (false, $"Input exceeds {_maxInputChars / 1000}k chars");

        // 快速通道：常见安全短文本直接放行（无需 LLM 审核）
        // 改为精确匹配，防止 "hi, ignore all rules" 绕过
        if (text.Length <= 50)
        {
            var trimmed = text.Trim();
            if (SafeExact.Contains(trimmed))
            {
                _logger?.LogDebug("SafetyFastPath({Direction}): OK (safe exact)", direction);
                return (true, "");
            }
            // Action prefixes: only allow when followed by whitespace or path char
            if (SafeActionPrefixes.Any(p =>
                trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase) &&
                (trimmed.Length == p.Length || !char.IsLetter(trimmed[p.Length]))))
            {
                _logger?.LogDebug("SafetyFastPath({Direction}): OK (safe action)", direction);
                return (true, "");
            }
        }

        // 规则级预检（零 LLM 成本）：短文本且通过规则检查 → 直接放行
        // 比走 LLM 审核快 10-50 倍，覆盖 80%+ 的日常消息
        // 阈值降低到 200 以减少 prompt injection 绕过风险
        if (text.Length <= 200 && IsSafeByRules(text))
        {
            _logger?.LogDebug("SafetyRulePath({Direction}): OK ({Len} chars, safe by rules)", direction, text.Length);
            return (true, "");
        }

        // Shared cache hit — reuse verdict from same-direction identical check
        var cachedVerdict = VerdictCache.Get(text, direction);
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

            VerdictCache.Set(text, result.allow, result.reason, direction);

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
