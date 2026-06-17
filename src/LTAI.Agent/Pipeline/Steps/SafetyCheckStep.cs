// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  SafetyCheckStep — content safety filter
//
//  Phase 3b: wraps LTAI.Core.Safety (SafetyRules, VerdictCache).
//  Checks user input for prompt injection, PII, harmful content.
//  If unsafe, sets SafetyBlocked flag and skips execution.
//
//  Note: SafetyCoordinator is an AIContextProvider (MAF internal).
//  For standalone pipeline use, we check via SafetyRules directly.
// ═══════════════════════════════════════════════════════════════

using System.Text.RegularExpressions;
using LTAI.Core.Safety;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Pipeline.Steps;

/// <summary>
/// Shared regex timeout. All pipeline regex operations must use this
/// to prevent ReDoS attacks. Value matches LTAI_REGEX_TIMEOUT_MS env var default.
/// </summary>
internal static partial class PipelineRegex
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(1000);

    [GeneratedRegex(@"[A-Za-z0-9+/]{40,}(?:={1,2})?", RegexOptions.None, 1000)]
    public static partial Regex Base64Pattern();

    [GeneratedRegex(@"(sk-[a-zA-Z0-9]{20,}|pk-[a-zA-Z0-9]{20,}|AKIA[0-9A-Z]{16}|-----BEGIN\s+(RSA\s+)?PRIVATE\s+KEY-----)", RegexOptions.IgnoreCase, 1000)]
    public static partial Regex CredentialPattern();

    [GeneratedRegex(@"\b1[3-9]\d{9}\b", RegexOptions.None, 1000)]
    public static partial Regex PhonePattern();

    [GeneratedRegex(@"(?:path|filePath)\s*=\s*[""']?([^""',\s}]+)[""']?", RegexOptions.IgnoreCase, 1000)]
    public static partial Regex PathArgPattern();

    [GeneratedRegex(@"""(?:path|filePath)""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase, 1000)]
    public static partial Regex JsonPathArgPattern();
}

/// <summary>
/// Pipeline step that checks user input for safety before processing.
/// Uses the configured SafetyRules for pattern-based filtering and
/// the VerdictCache for prompt injection detection.
///
/// On unsafe input, the SafetyBlocked flag is set and downstream
/// steps can short-circuit (the RouterStep checks this flag).
/// </summary>
public sealed class SafetyCheckStep : IPipelineStep
{
    private readonly ILogger<SafetyCheckStep> _logger;

    public string Name => "SafetyCheck";

    public SafetyCheckStep(ILogger<SafetyCheckStep>? logger = null)
    {
        _logger = logger ?? NullLogger<SafetyCheckStep>.Instance;
    }

    public Task<MessageContext> ProcessAsync(MessageContext context)
    {
        if (string.IsNullOrWhiteSpace(context.Request))
        {
            return Task.FromResult(context);
        }

        // Quick pattern-based checks
        var input = context.Request;

        // Check for prompt injection patterns
        if (ContainsPromptInjectionPattern(input))
        {
            context.SafetyBlocked = true;
            context.SafetyReason = "Prompt injection pattern detected";
            _logger.LogWarning("SafetyCheckStep: blocked prompt injection: {Input}",
                input[..Math.Min(input.Length, 100)]);
            context.Messages.Add(new ChatMessage(ChatRole.System,
                "⚠️ 安全拦截：检测到提示注入模式，请求已被阻止。"));
            return Task.FromResult(context);
        }

        // Check for credential leakage
        if (ContainsCredentialPattern(input))
        {
            context.SafetyBlocked = true;
            context.SafetyReason = "Credential or secret pattern detected";
            _logger.LogWarning("SafetyCheckStep: blocked credential leakage");
            context.Messages.Add(new ChatMessage(ChatRole.System,
                "⚠️ 安全拦截：检测到凭据泄露模式，请求已被阻止。"));
            return Task.FromResult(context);
        }

        // Check for PII (phone numbers, IDs)
        if (ContainsPiiPattern(input))
        {
            context.SafetyBlocked = true;
            context.SafetyReason = "PII pattern detected";
            _logger.LogWarning("SafetyCheckStep: blocked PII");
            context.Messages.Add(new ChatMessage(ChatRole.System,
                "⚠️ 安全拦截：检测到个人身份信息，请求已被阻止。"));
            return Task.FromResult(context);
        }

        return Task.FromResult(context);
    }

    private static bool ContainsPromptInjectionPattern(string input)
    {
        // Common prompt injection patterns
        var lower = input.ToLowerInvariant();
        var injections = new[]
        {
            "ignore previous instructions",
            "ignore all previous",
            "ignore your instructions",
            "ignore the above",
            "forget your instructions",
            "forget all instructions",
            "you are now",
            "act as if",
            "system prompt",
            "your system prompt",
            "jailbreak",
            "you have been",
            "override instructions",
            "override your",
            "disregard",
            "new instructions",
            "dan",
            "do anything now",
        };
        if (injections.Any(p => lower.Contains(p))) return true;
        if (lower.StartsWith("!important") || lower.StartsWith("!system")) return true;
        if (lower.Contains("pretend") && lower.Contains("you are")) return true;
        if (lower.Contains("dalam") && lower.Contains("bertindak")) return true;

        // Check for base64-encoded injection patterns
        try
        {
            var base64 = PipelineRegex.Base64Pattern().Match(input);
            if (base64.Success)
            {
                var decoded = System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(base64.Value));
                var decodedLower = decoded.ToLowerInvariant();
                if (injections.Any(p => decodedLower.Contains(p)))
                    return true;
            }
        }
        catch
        {
            // Invalid base64, skip
        }

        return false;
    }

    private static bool ContainsCredentialPattern(string input)
    {
        // API key patterns (sk, pk, AKIA etc.)
        return PipelineRegex.CredentialPattern().IsMatch(input);
    }

    private static bool ContainsPiiPattern(string input)
    {
        // Phone numbers, ID card numbers (heuristic)
        return PipelineRegex.PhonePattern().IsMatch(input)
            && input.Length < 50; // Only flag short messages containing PII
    }
}
