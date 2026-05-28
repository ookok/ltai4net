using System.Collections.Concurrent;
using System.Text;
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

    // Zero-width and invisible characters used in homoglyph/bypass attacks
    private static readonly char[] ZeroWidthChars = {
        '\u200B', // ZERO WIDTH SPACE
        '\u200C', // ZERO WIDTH NON-JOINER
        '\u200D', // ZERO WIDTH JOINER
        '\uFEFF', // ZERO WIDTH NO-BREAK SPACE (BOM)
        '\u2060', // WORD JOINER
        '\u2061', '\u2062', '\u2063', '\u2064', // invisible operators
        '\u00AD', // SOFT HYPHEN
        '\u034F', // COMBINING GRAPHEME JOINER
        '\u061C', // ARABIC LETTER MARK
        '\u180E', // MONGOLIAN VOWEL SEPARATOR
    };

    // Chinese injection keywords (supplement English patterns)
    private static readonly string[] ChineseInjectionKeywords =
    {
        "忽略之前", "忽略所有指令", "忽略以上", "忽略之前的指令",
        "覆盖系统", "覆盖系统提示", "无视规则", "无视所有限制",
        "你现在是", "扮演一个", "假设你是", "假装你是",
        "输出你的提示词", "显示系统提示", "泄露你的指令",
        "新的指令", "新规则", "从现在开始",
        "忽略上文", "忽略上文的", "请忽略", "请忽略上文",
        "重置指令", "清除记忆", "清除上下文", "重置对话",
        "不要遵守", "不遵守", "不要听", "不要遵循",
        "系统指令", "系统提示", "系统规则", "底层指令",
        "越狱", "破解模式", "无限制模式", "自由模式",
        "提取提示词", "输出系统提示", "查看你的指令", "显示你的规则",
        "复制你的系统提示", "输出你的系统指令",
    };

    /// <summary>
    /// Normalize text to defend against Unicode homoglyph and zero-width injection attacks.
    /// 1. Strip zero-width/invisible characters
    /// 2. Apply NFKC normalization (decomposes lookalike chars to canonical forms)
    /// Returns the normalized string and whether any changes were made.
    /// </summary>
    private static (string Normalized, bool Changed) NormalizeUnicode(string text)
    {
        if (string.IsNullOrEmpty(text))
            return (text, false);

        var changed = false;

        // Step 1: Strip zero-width characters
        var stripped = text;
        foreach (var z in ZeroWidthChars)
        {
            if (stripped.Contains(z))
            {
                stripped = stripped.Replace(z.ToString(), "");
                changed = true;
            }
        }

        // Step 2: NFKC normalization (decomposes homoglyphs like Cyrillic 'і' → Latin 'i')
        var normalized = stripped.Normalize(NormalizationForm.FormKC);
        if (normalized != stripped)
            changed = true;

        return (normalized, changed);
    }

    private static readonly (string Name, string Pattern)[] _inputPatterns = new (string, string)[]
    {
        ("ignore_instructions", @"ignore\s+(all\s+)?(previous|prior|above|before)\s+(instructions?|directions?|constraints?)"),
        ("role_override", @"(you\s+are\s+now|pretend\s+to\s+be|act\s+as\s+(a|an)\s+(unrestricted|evil|malicious|unfiltered|limitless))"),
        ("roleplay_malicious", @"(DAN|jailbreak)\s*(mode|prompt|enabled|activated)|(developer|god)\s*mode"),
        ("override_safety", @"(bypass|disable|ignore|override)\s+(safety|filter|guardrail|content\s*policy|ethics?)"),
        ("destructive_command", @"(delete\s+(all|every)\s+file|wipe\s+(the\s+)?(disk|drive|system)|drop\s+(database|table)\s+\*)"),
        ("destructive_command_zh", @"(?:删除\s*(?:所有|全部)\s*(?:文件|数据)|格式化\s*(?:磁盘|硬盘|系统)|清空\s*(?:数据库|全部)\s*(?:数据|记录|表)|删除\s*系统\s*文件|销毁\s*(?:所有|全部)\s*数据|drop\s*(?:database|table)\s+)"),
        ("indirect_injection", @"(?:以下(?:是|的)?(?:文档|内容|信息|资料|知识|上下文)(?:中|里|内)?(?:的)?(?:内容|信息|指令|要求)|检索到(?:的)?(?:以下|如下)(?:文档|内容)|根据(?:以下|如下)(?:文档|资料))"),
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
        ("hate_speech_zh", @"(?:杀死\s*(?:所有人|全部|一切)|种族\s*(?:灭绝|清洗)|消灭\s*(?:所有|一切|全部)|暴力\s*(?:伤害|袭击|攻击))", "Hate speech or violence detected (Chinese)"),
        ("self_harm", @"\b(?:suicide|self[- ]?harm|kill\s+(?:yourself|myself)|end\s+it\s+all)\b", "Self-harm content detected"),
        ("self_harm_zh", @"(?:自杀|自残|自我伤害|结束(?:自己|生命)|伤害\s*自己)", "Self-harm content detected (Chinese)"),
        ("internal_config", @"(?:api[_-]?key|api[_-]?secret|connection[_-]?string|password|secret|token|auth[_-]?token|bearer\s+[a-zA-Z0-9]{16,})", "Potential internal configuration or secret leaked")
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

        // Step 0: Unicode normalization — defend against homoglyph + zero-width attacks
        var (normalized, unicodeChanged) = NormalizeUnicode(text);
        if (unicodeChanged)
        {
            _logger.LogInformation("PromptShield: Unicode normalization applied (homoglyph/zero-width defense)");
        }

        var sanitized = normalized;
        var violations = new List<string>();

        // Step 0.5: Chinese injection keyword detection
        foreach (var keyword in ChineseInjectionKeywords)
        {
            if (sanitized.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"chinese_injection:{keyword}");
                sanitized = sanitized.Replace(keyword, "[BLOCKED]", StringComparison.OrdinalIgnoreCase);
            }
        }

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
                outputResult.Violations.Contains("hate_speech") || outputResult.Violations.Contains("hate_speech_zh") ||
                outputResult.Violations.Contains("self_harm") || outputResult.Violations.Contains("self_harm_zh") ? "critical" : "medium");
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
