using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.System;

public sealed record ShieldResult
{
    public bool Passed { get; init; }
    public string Layer { get; init; } = string.Empty;
    public List<string> Violations { get; init; } = new();
    public string SanitizedText { get; init; } = string.Empty;
    public string OriginalText { get; init; } = string.Empty;
    public string? Warning { get; init; }
}

public sealed record HITLRequest
{
    public string RequestId { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string RiskLevel { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public bool Approved { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
}

public sealed class PromptShield
{
    private static readonly Lazy<PromptShield> _instance = new(() => new PromptShield(AutoLogger<PromptShield>.Create()));
    public static PromptShield Instance => _instance.Value;

    private readonly ILogger<PromptShield> _logger;
    private readonly object _statsLock = new();
    private int _violationsCaught;
    private readonly ConcurrentQueue<HITLRequest> _hitlQueue = new();

    private static readonly (string Name, string Pattern)[] _inputPatterns = new (string, string)[]
    {
        ("ignore_instructions", @"ignore\s+(all\s+)?(previous|prior|above|before)\s+(instructions?|directions?|constraints?)"),
        ("role_override", @"(you\s+are\s+now|pretend\s+to\s+be|act\s+as\s+(a|an)\s+(unrestricted|evil|malicious|unfiltered|limitless))"),
        ("roleplay_malicious", @"(DAN|jailbreak)\s*(mode|prompt|enabled|activated)|(developer|god)\s*mode"),
        ("override_safety", @"(bypass|disable|ignore|override)\s+(safety|filter|guardrail|content\s*policy|ethics?)"),
        ("destructive_command", @"(delete\s+(all|every)\s+file|wipe\s+(the\s+)?(disk|drive|system)|drop\s+(database|table)\s+\*)"),
        ("privilege_escalation", @"(sudo|admin|root)\s+(access|privilege|right|permission)"),
        ("token_boundary", @"<\|endoftext\|>|<\|im_start\|>|<\|im_end\|>|</?system>"),
        ("template_injection", @"\{\{.*?[^{}\w].*?\}\}|\{%\s*(if|for|include|extends|block)"),
        ("code_execution", @"(exec\(|eval\(|system\(|shell_exec\(|subprocess\.)"),
        ("remote_code", @"(curl|wget)\s+.*\|.*(sh|bash|python|perl|ruby)")
    };

    private static readonly (string Name, string Pattern, string Warning)[] _outputChecks = new (string, string, string)[]
    {
        ("credit_card", @"\b(?:\d[ -]*?){13,16}\b", "Potential credit card number in output"),
        ("api_keys", @"(sk-[a-zA-Z0-9]{20,}|AIza[0-9A-Za-z\-_]{35}|AKIA[0-9A-Z]{16}|ghp_[a-zA-Z0-9]{36})", "Potential API key in output"),
        ("harmful_urls", @"https?://(?:pastebin\.com|bit\.ly|tinyurl\.com|shorturl\.at)/(?:raw|dl)/\w+", "Suspicious URL detected"),
        ("hate_speech", @"\b(?:kill\s+(?:all|every(?:one|body))|exterminate\s+(?:all|every)|ethnic\s+cleansing)\b", "Hate speech or violence detected"),
        ("self_harm", @"\b(?:suicide|self[- ]?harm|kill\s+(?:yourself|myself)|end\s+it\s+all)\b", "Self-harm content detected")
    };

    public PromptShield(ILogger<PromptShield> logger)
    {
        _logger = logger;
    }

    public ShieldResult SanitizeInput(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ShieldResult { Passed = true, Layer = "input", OriginalText = text ?? string.Empty, SanitizedText = text ?? string.Empty };
        }

        var sanitized = text;
        var violations = new List<string>();

        foreach (var (name, pattern) in _inputPatterns)
        {
            try
            {
                var matches = Regex.Matches(sanitized, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (matches.Count > 0)
                {
                    violations.Add(name);
                    sanitized = Regex.Replace(sanitized, pattern, "[BLOCKED]", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Regex error for pattern {Pattern}", name);
            }
        }

        var passed = violations.Count == 0;

        if (!passed)
        {
            lock (_statsLock) { _violationsCaught++; }
            _logger.LogWarning("Input shield blocked {Count} violations: {Violations}", violations.Count, string.Join(", ", violations));
        }

        return new ShieldResult
        {
            Passed = passed,
            Layer = "input",
            Violations = violations,
            SanitizedText = passed ? text : sanitized,
            OriginalText = text
        };
    }

    public ShieldResult CheckOutput(string text, string context = "public")
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ShieldResult { Passed = true, Layer = "output", OriginalText = text ?? string.Empty, SanitizedText = text ?? string.Empty };
        }

        var violations = new List<string>();
        var warnings = new List<string>();

        foreach (var (name, pattern, warning) in _outputChecks)
        {
            try
            {
                if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase))
                {
                    violations.Add(name);
                    warnings.Add(warning);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Regex error for output check {Pattern}", name);
            }
        }

        var passed = violations.Count == 0;

        if (!passed)
        {
            lock (_statsLock) { _violationsCaught++; }
            _logger.LogWarning("Output shield detected {Count} violations: {Violations}", violations.Count, string.Join(", ", violations));
        }

        return new ShieldResult
        {
            Passed = passed,
            Layer = "output",
            Violations = violations,
            SanitizedText = passed ? text : "[FILTERED]",
            OriginalText = text,
            Warning = warnings.Count > 0 ? string.Join("; ", warnings) : null
        };
    }

    public HITLRequest Escalate(string operation, string detail, string userId, string riskLevel)
    {
        var request = new HITLRequest
        {
            RequestId = Guid.NewGuid().ToString("N")[..12],
            UserId = userId,
            Operation = operation,
            Detail = detail,
            RiskLevel = riskLevel,
            Timestamp = DateTime.UtcNow
        };

        _hitlQueue.Enqueue(request);

        while (_hitlQueue.Count > 50)
        {
            _hitlQueue.TryDequeue(out _);
        }

        _logger.LogWarning("HITL escalated: {Operation} (risk={RiskLevel}, user={UserId}, id={RequestId})",
            operation, riskLevel, userId, request.RequestId);

        return request;
    }

    public bool Approve(string requestId, string userId)
    {
        var requests = _hitlQueue.ToArray();
        for (int i = 0; i < requests.Length; i++)
        {
            if (requests[i].RequestId == requestId && !requests[i].Approved)
            {
                requests[i].Approved = true;
                requests[i].ApprovedBy = userId;
                requests[i].ApprovedAt = DateTime.UtcNow;
                _logger.LogInformation("HITL request {RequestId} approved by {UserId}", requestId, userId);
                return true;
            }
        }
        _logger.LogWarning("HITL request {RequestId} not found or already resolved", requestId);
        return false;
    }

    public bool Reject(string requestId, string userId)
    {
        var requests = _hitlQueue.ToArray();
        for (int i = 0; i < requests.Length; i++)
        {
            if (requests[i].RequestId == requestId && !requests[i].Approved)
            {
                requests[i].Approved = true;
                requests[i].ApprovedBy = userId;
                requests[i].ApprovedAt = DateTime.UtcNow;
                _logger.LogInformation("HITL request {RequestId} rejected by {UserId}", requestId, userId);
                return true;
            }
        }
        _logger.LogWarning("HITL request {RequestId} not found or already resolved", requestId);
        return false;
    }

    public List<HITLRequest> PendingRequests()
    {
        return _hitlQueue.Where(r => !r.Approved).ToList();
    }

    public ShieldResult Defend(string userInput, string llmOutput)
    {
        var inputResult = SanitizeInput(userInput);
        if (!inputResult.Passed)
        {
            Escalate("prompt_injection", $"Input shield blocked: {string.Join(", ", inputResult.Violations)}", "system", "high");
            return inputResult;
        }

        var outputResult = CheckOutput(llmOutput);
        if (!outputResult.Passed)
        {
            Escalate("output_violation", $"Output shield detected: {string.Join(", ", outputResult.Violations)}", "system",
                outputResult.Violations.Contains("hate_speech") || outputResult.Violations.Contains("self_harm") ? "critical" : "medium");
        }

        return outputResult;
    }

    public (int ViolationsCaught, int HitlQueueSize) Stats()
    {
        int violations;
        lock (_statsLock)
        {
            violations = _violationsCaught;
        }
        return (violations, _hitlQueue.Count);
    }

    public static bool HasPromptInjectionPattern(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var (_, pattern) in _inputPatterns)
        {
            try
            {
                if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline))
                    return true;
            }
            catch { }
        }
        return false;
    }

    public static IReadOnlyList<(string Name, string Pattern)> InputPatterns => _inputPatterns;
}

file static class AutoLogger<T>
{
    public static ILogger<T> Create()
    {
        return NullLoggerFactory.Instance.CreateLogger<T>();
    }
}
