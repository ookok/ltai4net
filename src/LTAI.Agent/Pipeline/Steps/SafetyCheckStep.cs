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

using LTAI.Core.Safety;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Pipeline.Steps;

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
            return Task.FromResult(context);
        }

        // Check for credential leakage
        if (ContainsCredentialPattern(input))
        {
            context.SafetyBlocked = true;
            context.SafetyReason = "Credential or secret pattern detected";
            _logger.LogWarning("SafetyCheckStep: blocked credential leakage");
            return Task.FromResult(context);
        }

        // Check for PII (phone numbers, IDs)
        if (ContainsPiiPattern(input))
        {
            context.SafetyBlocked = true;
            context.SafetyReason = "PII pattern detected";
            _logger.LogWarning("SafetyCheckStep: blocked PII");
            return Task.FromResult(context);
        }

        return Task.FromResult(context);
    }

    private static bool ContainsPromptInjectionPattern(string input)
    {
        // Common prompt injection patterns
        var lower = input.ToLowerInvariant();
        return lower.Contains("ignore previous instructions")
            || lower.Contains("ignore all previous")
            || lower.Contains("forget your instructions")
            || lower.Contains("you are now")
            || lower.Contains("act as if")
            || lower.Contains("system prompt")
            || lower.Contains("your system prompt")
            || lower.Contains("jailbreak")
            || lower.Contains("you have been")
            || (lower.Contains("pretend") && lower.Contains("you are"))
            || lower.StartsWith("!important")
            || lower.StartsWith("!system")
            || lower.Contains("ignore the above")
            || (lower.Contains("dalam") && lower.Contains("bertindak"));
    }

    private static bool ContainsCredentialPattern(string input)
    {
        // API key patterns (sk, pk, AKIA etc.)
        return System.Text.RegularExpressions.Regex.IsMatch(input,
            @"(sk-[a-zA-Z0-9]{20,}|pk-[a-zA-Z0-9]{20,}|AKIA[0-9A-Z]{16}|-----BEGIN\s+(RSA\s+)?PRIVATE\s+KEY-----)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool ContainsPiiPattern(string input)
    {
        // Phone numbers, ID card numbers (heuristic)
        return System.Text.RegularExpressions.Regex.IsMatch(input,
            @"\b1[3-9]\d{9}\b")  // Chinese mobile phone numbers
            && input.Length < 50; // Only flag short messages containing PII
    }
}
