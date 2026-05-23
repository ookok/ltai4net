using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LTAI.DNA.Safety;

public enum PolicyAction { Block, Warn, Redact, Log, Audit }

public enum PolicyCategory { Input, Output, DNAAlignment, Tool, Network }

public sealed record PolicyRule
{
    public string Id { get; init; } = "";
    public string Description { get; init; } = "";
    public string Condition { get; init; } = "";
    public PolicyAction Action { get; init; }
    public PolicyCategory Category { get; init; } = PolicyCategory.Input;
    public string? RedactionPattern { get; init; }
    public string? Message { get; init; }
    public double MinConfidence { get; init; } = 0.5;
    public int Priority { get; init; } = 100;
    public bool Enabled { get; set; } = true;
    public DateTime? EffectiveFrom { get; init; }
    public DateTime? EffectiveUntil { get; init; }
    public List<string> Tags { get; init; } = new();
}

public sealed class PolicyEvaluation
{
    public string RuleId { get; init; } = "";
    public bool Triggered { get; init; }
    public PolicyAction Action { get; init; }
    public string? Message { get; init; }
    public string? MatchedContent { get; init; }
    public double Confidence { get; init; } = 1.0;
    public DateTime EvaluatedAt { get; init; } = DateTime.UtcNow;
}

public sealed class PolicyVersion
{
    public string Version { get; init; } = "1.0.0";
    public string Hash { get; init; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public string? Description { get; init; }
    public bool IsActive { get; set; } = true;
    public int CanaryPercentage { get; set; } = 0;
    public Dictionary<string, int> Metrics { get; init; } = new();
}

public sealed class PolicyMetrics
{
    public int TotalEvaluations { get; set; }
    public int TotalTriggered { get; set; }
    public int TotalBlocked { get; set; }
    public int TotalWarned { get; set; }
    public int TotalRedacted { get; set; }
    public Dictionary<string, int> RuleHits { get; init; } = new();
    public Dictionary<string, int> FalsePositives { get; init; } = new();
}

public sealed class PolicyAsCode
{
    private readonly ILogger<PolicyAsCode>? _logger;
    private readonly List<PolicyRule> _inputRules = new();
    private readonly List<PolicyRule> _outputRules = new();
    private readonly List<PolicyRule> _dnaRules = new();
    private readonly List<PolicyRule> _toolRules = new();
    private readonly List<PolicyRule> _networkRules = new();
    private readonly List<PolicyVersion> _versions = new();
    private readonly PolicyMetrics _metrics = new();
    private PolicyVersion? _activeVersion;
    private PolicyVersion? _canaryVersion;

    public IReadOnlyList<PolicyRule> InputRules => _inputRules.AsReadOnly();
    public IReadOnlyList<PolicyRule> OutputRules => _outputRules.AsReadOnly();
    public IReadOnlyList<PolicyRule> DNARules => _dnaRules.AsReadOnly();
    public IReadOnlyList<PolicyVersion> Versions => _versions.AsReadOnly();
    public PolicyMetrics Metrics => _metrics;

    public PolicyAsCode(ILogger<PolicyAsCode>? logger = null)
    {
        _logger = logger;
    }

    public void LoadFromYaml(string yamlContent)
    {
        var lines = yamlContent.Split('\n');
        PolicyRule? currentRule = null;
        PolicyCategory currentCategory = PolicyCategory.Input;
        string? version = null;
        int canaryPercentage = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;

            var indent = line.Length - line.TrimStart().Length;
            var trimmed = line.Trim();

            if (trimmed.StartsWith("apiVersion:"))
            {
                version = trimmed.Split(':')[1].Trim();
                continue;
            }

            if (trimmed.StartsWith("canary_percentage:"))
            {
                if (int.TryParse(trimmed.Split(':')[1].Trim(), out var pct))
                    canaryPercentage = pct;
                continue;
            }

            if (trimmed.StartsWith("category:"))
            {
                currentCategory = trimmed.Split(':')[1].Trim().ToLowerInvariant() switch
                {
                    "input" => PolicyCategory.Input,
                    "output" => PolicyCategory.Output,
                    "dna" or "dna_alignment" => PolicyCategory.DNAAlignment,
                    "tool" => PolicyCategory.Tool,
                    "network" => PolicyCategory.Network,
                    _ => PolicyCategory.Input
                };
                continue;
            }

            if (trimmed.StartsWith("- id:"))
            {
                if (currentRule != null) AddRuleToCategory(currentRule, currentCategory);
                currentRule = new PolicyRule
                {
                    Id = trimmed[5..].Trim(),
                    Category = currentCategory
                };
                continue;
            }

            if (currentRule != null && trimmed.Contains(':'))
            {
                var colonIdx = trimmed.IndexOf(':');
                var key = trimmed[..colonIdx].Trim();
                var value = trimmed[(colonIdx + 1)..].Trim().Trim('"', '\'');

                currentRule = key switch
                {
                    "description" => currentRule with { Description = value },
                    "condition" => currentRule with { Condition = value },
                    "action" => currentRule with { Action = ParseAction(value) },
                    "redaction_pattern" => currentRule with { RedactionPattern = value },
                    "message" => currentRule with { Message = value },
                    "min_confidence" => double.TryParse(value, out var mc) ? currentRule with { MinConfidence = mc } : currentRule,
                    "priority" => int.TryParse(value, out var p) ? currentRule with { Priority = p } : currentRule,
                    "enabled" => bool.TryParse(value, out var e) ? currentRule with { Enabled = e } : currentRule,
                    "effective_from" => DateTime.TryParse(value, out var ef) ? currentRule with { EffectiveFrom = ef } : currentRule,
                    "effective_until" => DateTime.TryParse(value, out var eu) ? currentRule with { EffectiveUntil = eu } : currentRule,
                    _ => currentRule
                };
            }
        }

        if (currentRule != null) AddRuleToCategory(currentRule, currentCategory);

        var newVersion = new PolicyVersion
        {
            Version = version ?? "1.0.0",
            Hash = ComputeHash(yamlContent),
            CreatedAt = DateTime.UtcNow,
            Description = $"Loaded from YAML with {GetTotalRuleCount()} rules",
            CanaryPercentage = canaryPercentage
        };

        RegisterVersion(newVersion);
        _logger?.LogInformation("PolicyAsCode: Loaded v{Version} with {Count} rules (canary={Canary}%)",
            newVersion.Version, GetTotalRuleCount(), canaryPercentage);
    }

    public void LoadFromJson(string policyJson)
    {
        var doc = JsonDocument.Parse(policyJson);
        if (doc.RootElement.TryGetProperty("policies", out var policies))
        {
            foreach (var policy in policies.EnumerateArray())
            {
                var name = policy.GetProperty("name").GetString() ?? "";
                var rules = policy.GetProperty("rules");

                foreach (var rule in rules.EnumerateArray())
                {
                    var r = new PolicyRule
                    {
                        Id = rule.GetProperty("id").GetString() ?? "",
                        Description = rule.GetProperty("description").GetString() ?? "",
                        Condition = rule.GetProperty("condition").GetString() ?? "",
                        Action = rule.GetProperty("action").GetString() switch
                        {
                            "block" => PolicyAction.Block,
                            "warn" => PolicyAction.Warn,
                            "redact" => PolicyAction.Redact,
                            "audit" => PolicyAction.Audit,
                            _ => PolicyAction.Log
                        },
                        RedactionPattern = rule.TryGetProperty("redaction_pattern", out var rp) ? rp.GetString() : null,
                        Message = rule.TryGetProperty("message", out var msg) ? msg.GetString() : null
                    };

                    var category = name switch
                    {
                        "content_safety" or "input_safety" => PolicyCategory.Input,
                        "output_safety" => PolicyCategory.Output,
                        "dna_alignment" => PolicyCategory.DNAAlignment,
                        "tool_safety" => PolicyCategory.Tool,
                        "network_safety" => PolicyCategory.Network,
                        _ => PolicyCategory.Input
                    };

                    AddRuleToCategory(r, category);
                }
            }
        }
    }

    public void LoadDefaults()
    {
        _inputRules.Clear();
        _outputRules.Clear();
        _dnaRules.Clear();
        _toolRules.Clear();
        _networkRules.Clear();

        _inputRules.AddRange(new[]
        {
            new PolicyRule
            {
                Id = "CS-001", Description = "拒绝越狱提示词注入",
                Condition = "input.matches('ignore.*(instruction|system|prompt)', i)",
                Action = PolicyAction.Block, Priority = 10,
                Message = "Prompt injection detected. Request blocked.",
                Tags = new List<string> { "prompt-injection", "jailbreak" }
            },
            new PolicyRule
            {
                Id = "CS-002", Description = "拒绝生成恶意代码",
                Condition = "input.contains('hack') || input.contains('exploit') || input.contains('malware')",
                Action = PolicyAction.Block, Priority = 10,
                Message = "Malicious code generation request blocked.",
                Tags = new List<string> { "malicious-code", "security" }
            },
            new PolicyRule
            {
                Id = "CS-003", Description = "拒绝编码绕过攻击",
                Condition = "input.decoded('base64').matches('ignore|override|system', i)",
                Action = PolicyAction.Block, Priority = 15,
                Message = "Encoded injection detected. Request blocked.",
                Tags = new List<string> { "encoding-bypass", "base64" }
            },
            new PolicyRule
            {
                Id = "CS-004", Description = "中文恶意模式检测",
                Condition = "input.chinese.matches('执行系统命令|删除所有文件|格式化硬盘|提权攻击|越权访问', i)",
                Action = PolicyAction.Block, Priority = 10,
                Message = "中文恶意模式匹配，请求已拦截。",
                Tags = new List<string> { "chinese-malicious", "command-injection" }
            },
            new PolicyRule
            {
                Id = "CS-005", Description = "分块注入检测",
                Condition = "input.cumulative_risk > 0.6",
                Action = PolicyAction.Block, Priority = 20,
                Message = "Cumulative injection risk detected. Request blocked.",
                Tags = new List<string> { "chunked-injection", "cumulative" }
            }
        });

        _outputRules.AddRange(new[]
        {
            new PolicyRule
            {
                Id = "OS-001", Description = "检测敏感信息泄露",
                Condition = @"output.matches('(api[_-]?key|secret|token|password)\s*[:=]\s*[\w-]{8,}', i)",
                Action = PolicyAction.Redact, Priority = 20,
                RedactionPattern = "***REDACTED***",
                Message = "Sensitive information redacted from output.",
                Tags = new List<string> { "credential-leak", "pii" }
            },
            new PolicyRule
            {
                Id = "OS-002", Description = "EIA 报告合规检查",
                Condition = "output.is_eia && !output.has_section('references')",
                Action = PolicyAction.Warn, Priority = 50,
                Message = "EIA report is missing standards reference section.",
                Tags = new List<string> { "eia-compliance", "regulatory" }
            },
            new PolicyRule
            {
                Id = "OS-003", Description = "SQL 注入检测",
                Condition = @"output.matches('(DROP|DELETE|INSERT|UPDATE)\s+(TABLE|FROM|INTO)', i)",
                Action = PolicyAction.Redact, Priority = 15,
                RedactionPattern = "[SQL filtered]",
                Message = "SQL injection pattern detected and redacted.",
                Tags = new List<string> { "sql-injection", "security" }
            },
            new PolicyRule
            {
                Id = "OS-004", Description = "路径遍历检测",
                Condition = @"output.matches('(\.\./){2,}', i)",
                Action = PolicyAction.Redact, Priority = 15,
                RedactionPattern = "[path-traversal filtered]",
                Message = "Path traversal pattern redacted.",
                Tags = new List<string> { "path-traversal", "security" }
            },
            new PolicyRule
            {
                Id = "OS-005", Description = "命令注入检测",
                Condition = @"output.matches('(rm\s+-rf|format\s+/|del\s+/[fs])', i)",
                Action = PolicyAction.Redact, Priority = 15,
                RedactionPattern = "[command filtered]",
                Message = "Dangerous command pattern redacted.",
                Tags = new List<string> { "command-injection", "security" }
            },
            new PolicyRule
            {
                Id = "OS-006", Description = "私钥泄露检测",
                Condition = @"output.matches('-----BEGIN\s+(RSA\s+)?PRIVATE\s+KEY-----', i)",
                Action = PolicyAction.Redact, Priority = 10,
                RedactionPattern = "[private-key redacted]",
                Message = "Private key redacted from output.",
                Tags = new List<string> { "private-key", "credential-leak" }
            }
        });

        _dnaRules.AddRange(new[]
        {
            new PolicyRule
            {
                Id = "DNA-001", Description = "保持人格一致性",
                Condition = "output.deviates_from_persona",
                Action = PolicyAction.Warn, Priority = 50,
                Message = "Response may deviate from configured persona.",
                Tags = new List<string> { "persona-drift", "identity" }
            },
            new PolicyRule
            {
                Id = "DNA-002", Description = "防止人格漂移",
                Condition = "output.contains('As an AI') || output.contains('I cannot') || output.contains('I am just a program')",
                Action = PolicyAction.Warn, Priority = 40,
                Message = "Response contains anti-persona language.",
                Tags = new List<string> { "persona-drift", "anti-persona" }
            },
            new PolicyRule
            {
                Id = "DNA-003", Description = "中文人格漂移检测",
                Condition = "output.contains('我是AI') || output.contains('我无法') || output.contains('我只是个程序')",
                Action = PolicyAction.Warn, Priority = 40,
                Message = "响应包含反人格语言。",
                Tags = new List<string> { "persona-drift", "chinese" }
            }
        });

        _toolRules.AddRange(new[]
        {
            new PolicyRule
            {
                Id = "TOOL-001", Description = "阻止管道到 shell 执行",
                Condition = @"tool.input.matches('\|\s*(bash|sh|pwsh|zsh|fish|cmd)', i)",
                Action = PolicyAction.Block, Priority = 10,
                Message = "Pipe-to-shell execution blocked.",
                Tags = new List<string> { "shell-injection", "pipe-to-shell" }
            },
            new PolicyRule
            {
                Id = "TOOL-002", Description = "阻止下载并执行模式",
                Condition = @"tool.input.matches('(curl|wget)\s+\S+.*\|\s*\w+', i)",
                Action = PolicyAction.Block, Priority = 10,
                Message = "Download-and-execute pattern blocked.",
                Tags = new List<string> { "download-execute", "remote-code" }
            }
        });

        _networkRules.AddRange(new[]
        {
            new PolicyRule
            {
                Id = "NET-001", Description = "大数据传输警告",
                Condition = "network.size_kb > 1024",
                Action = PolicyAction.Warn, Priority = 60,
                Message = "Large outbound data transfer detected.",
                Tags = new List<string> { "data-exfiltration", "network" }
            },
            new PolicyRule
            {
                Id = "NET-002", Description = "阻止可疑外联",
                Condition = @"network.url.matches('(pastebin|ngrok|localhost:\d{4,5})', i)",
                Action = PolicyAction.Block, Priority = 20,
                Message = "Suspicious outbound connection blocked.",
                Tags = new List<string> { "suspicious-domain", "c2-server" }
            }
        });

        var defaultVersion = new PolicyVersion
        {
            Version = "1.0.0",
            Hash = ComputeHash("defaults"),
            CreatedAt = DateTime.UtcNow,
            Description = $"Default policies with {GetTotalRuleCount()} rules"
        };

        RegisterVersion(defaultVersion);
        _logger?.LogInformation("PolicyAsCode: Loaded defaults with {Count} rules", GetTotalRuleCount());
    }

    public void RegisterVersion(PolicyVersion version)
    {
        foreach (var v in _versions) v.IsActive = false;
        version.IsActive = true;
        _versions.Add(version);
        _activeVersion = version;

        if (version.CanaryPercentage > 0 && version.CanaryPercentage < 100)
            _canaryVersion = version;
    }

    public void RollbackToVersion(string version)
    {
        var target = _versions.FirstOrDefault(v => v.Version == version);
        if (target == null)
        {
            _logger?.LogWarning("PolicyAsCode: Rollback target v{Version} not found", version);
            return;
        }

        foreach (var v in _versions) v.IsActive = false;
        target.IsActive = true;
        _activeVersion = target;
        _logger?.LogInformation("PolicyAsCode: Rolled back to v{Version}", version);
    }

    public List<PolicyEvaluation> EvaluateInput(string text, string? sessionId = null)
    {
        _metrics.TotalEvaluations++;
        var results = new List<PolicyEvaluation>();
        var activeRules = GetActiveRules(_inputRules, sessionId);

        foreach (var rule in activeRules.OrderBy(r => r.Priority))
        {
            var (triggered, matchedContent, confidence) = EvaluateCondition(rule.Condition, text);
            if (triggered)
            {
                _metrics.TotalTriggered++;
                _metrics.RuleHits[rule.Id] = _metrics.RuleHits.GetValueOrDefault(rule.Id) + 1;

                if (rule.Action == PolicyAction.Block) _metrics.TotalBlocked++;
                else if (rule.Action == PolicyAction.Warn) _metrics.TotalWarned++;

                results.Add(new PolicyEvaluation
                {
                    RuleId = rule.Id,
                    Triggered = true,
                    Action = rule.Action,
                    Message = rule.Message,
                    MatchedContent = matchedContent,
                    Confidence = confidence
                });
            }
        }

        return results;
    }

    public List<PolicyEvaluation> EvaluateOutput(string text, string? context = null, string? sessionId = null)
    {
        _metrics.TotalEvaluations++;
        var results = new List<PolicyEvaluation>();
        var activeRules = GetActiveRules(_outputRules, sessionId);

        foreach (var rule in activeRules.OrderBy(r => r.Priority))
        {
            var (triggered, matchedContent, confidence) = EvaluateCondition(rule.Condition, text, context);
            if (triggered)
            {
                _metrics.TotalTriggered++;
                _metrics.RuleHits[rule.Id] = _metrics.RuleHits.GetValueOrDefault(rule.Id) + 1;

                if (rule.Action == PolicyAction.Redact) _metrics.TotalRedacted++;
                else if (rule.Action == PolicyAction.Warn) _metrics.TotalWarned++;

                results.Add(new PolicyEvaluation
                {
                    RuleId = rule.Id,
                    Triggered = true,
                    Action = rule.Action,
                    Message = rule.Message,
                    MatchedContent = matchedContent,
                    Confidence = confidence
                });
            }
        }

        return results;
    }

    public List<PolicyEvaluation> EvaluateDNAAlignment(string output, string? personaConfig = null)
    {
        _metrics.TotalEvaluations++;
        var results = new List<PolicyEvaluation>();

        foreach (var rule in _dnaRules.Where(r => r.Enabled).OrderBy(r => r.Priority))
        {
            var (triggered, matchedContent, confidence) = EvaluateCondition(rule.Condition, output, personaConfig);
            if (triggered)
            {
                _metrics.TotalTriggered++;
                _metrics.RuleHits[rule.Id] = _metrics.RuleHits.GetValueOrDefault(rule.Id) + 1;
                _metrics.TotalWarned++;

                results.Add(new PolicyEvaluation
                {
                    RuleId = rule.Id,
                    Triggered = true,
                    Action = rule.Action,
                    Message = rule.Message,
                    MatchedContent = matchedContent,
                    Confidence = confidence
                });
            }
        }

        return results;
    }

    public string ApplyRedactions(string text, List<PolicyEvaluation> evaluations)
    {
        foreach (var eval in evaluations.Where(e => e.Action == PolicyAction.Redact))
        {
            var rule = _outputRules.FirstOrDefault(r => r.Id == eval.RuleId);
            if (rule?.RedactionPattern is not null)
            {
                text = Regex.Replace(text, rule.Condition, rule.RedactionPattern, RegexOptions.IgnoreCase);
            }
        }

        return text;
    }

    public void ReportFalsePositive(string ruleId)
    {
        _metrics.FalsePositives[ruleId] = _metrics.FalsePositives.GetValueOrDefault(ruleId) + 1;
        _logger?.LogWarning("PolicyAsCode: False positive reported for rule {RuleId}", ruleId);
    }

    public Dictionary<string, object> GetStatus()
    {
        return new()
        {
            ["active_version"] = _activeVersion?.Version ?? "none",
            ["canary_version"] = _canaryVersion?.Version ?? "none",
            ["canary_percentage"] = _canaryVersion?.CanaryPercentage ?? 0,
            ["total_rules"] = GetTotalRuleCount(),
            ["input_rules"] = _inputRules.Count,
            ["output_rules"] = _outputRules.Count,
            ["dna_rules"] = _dnaRules.Count,
            ["tool_rules"] = _toolRules.Count,
            ["network_rules"] = _networkRules.Count,
            ["metrics"] = new
            {
                _metrics.TotalEvaluations,
                _metrics.TotalTriggered,
                _metrics.TotalBlocked,
                _metrics.TotalWarned,
                _metrics.TotalRedacted,
                TriggerRate = _metrics.TotalEvaluations > 0 ? (double)_metrics.TotalTriggered / _metrics.TotalEvaluations : 0
            },
            ["versions"] = _versions.Select(v => new
            {
                v.Version,
                v.CreatedAt,
                v.IsActive,
                v.CanaryPercentage,
                v.Description
            }).ToList()
        };
    }

    private void AddRuleToCategory(PolicyRule rule, PolicyCategory category)
    {
        var targetList = category switch
        {
            PolicyCategory.Input => _inputRules,
            PolicyCategory.Output => _outputRules,
            PolicyCategory.DNAAlignment => _dnaRules,
            PolicyCategory.Tool => _toolRules,
            PolicyCategory.Network => _networkRules,
            _ => _inputRules
        };

        targetList.Add(rule);
    }

    private int GetTotalRuleCount()
    {
        return _inputRules.Count + _outputRules.Count + _dnaRules.Count + _toolRules.Count + _networkRules.Count;
    }

    private List<PolicyRule> GetActiveRules(List<PolicyRule> rules, string? sessionId)
    {
        var now = DateTime.UtcNow;
        var active = rules.Where(r =>
            r.Enabled &&
            (r.EffectiveFrom == null || r.EffectiveFrom <= now) &&
            (r.EffectiveUntil == null || r.EffectiveUntil >= now)
        ).ToList();

        if (_canaryVersion != null && _canaryVersion.CanaryPercentage > 0 && sessionId != null)
        {
            var hash = Math.Abs(sessionId.GetHashCode()) % 100;
            if (hash >= _canaryVersion.CanaryPercentage)
            {
                active = active.Where(r => !_canaryVersion.Description?.Contains(r.Id) == true).ToList();
            }
        }

        return active;
    }

    private static PolicyAction ParseAction(string action)
    {
        return action.ToLowerInvariant() switch
        {
            "block" => PolicyAction.Block,
            "warn" => PolicyAction.Warn,
            "redact" => PolicyAction.Redact,
            "audit" => PolicyAction.Audit,
            "log" => PolicyAction.Log,
            _ => PolicyAction.Log
        };
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes)[..16];
    }

    private static (bool triggered, string? matchedContent, double confidence) EvaluateCondition(
        string condition, string text, string? context = null)
    {
        try
        {
            var lower = condition.ToLowerInvariant();

            if (lower.Contains("input.contains(") || lower.Contains("output.contains("))
            {
                var parts = lower.Split("||");
                foreach (var part in parts)
                {
                    var match = Regex.Match(part.Trim(), @"contains\('([^']*)'\)");
                    if (match.Success)
                    {
                        var searchStr = match.Groups[1].Value;
                        if (text.Contains(searchStr, StringComparison.OrdinalIgnoreCase))
                            return (true, searchStr, 0.9);
                    }
                }
            }

            if (lower.Contains(".matches("))
            {
                var match = Regex.Match(lower, @"matches\('([^']*)'[^)]*\)");
                if (match.Success)
                {
                    var pattern = match.Groups[1].Value;
                    var regexMatch = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
                    if (regexMatch.Success)
                        return (true, regexMatch.Value, 0.95);
                }
            }

            if (lower.Contains("input.decoded('base64')"))
            {
                var base64Match = Regex.Match(text, @"[A-Za-z0-9+/]{20,}={0,2}");
                if (base64Match.Success)
                {
                    try
                    {
                        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64Match.Value));
                        var innerMatch = Regex.Match(lower, @"decoded\('base64'\)\.matches\('([^']*)'");
                        if (innerMatch.Success && Regex.IsMatch(decoded, innerMatch.Groups[1].Value, RegexOptions.IgnoreCase))
                            return (true, $"base64:{base64Match.Value}", 0.98);
                    }
                    catch { }
                }
            }

            if (lower.Contains("input.chinese.matches("))
            {
                var match = Regex.Match(lower, @"chinese\.matches\('([^']*)'");
                if (match.Success)
                {
                    var patterns = match.Groups[1].Value.Split('|');
                    foreach (var pattern in patterns)
                    {
                        if (text.Contains(pattern.Trim(), StringComparison.OrdinalIgnoreCase))
                            return (true, pattern.Trim(), 0.95);
                    }
                }
            }

            if (lower.Contains("input.cumulative_risk"))
            {
                var match = Regex.Match(lower, @"cumulative_risk\s*>\s*([\d.]+)");
                if (match.Success && double.TryParse(match.Groups[1].Value, out var threshold))
                {
                    if (context != null && double.TryParse(context, out var risk))
                    {
                        if (risk > threshold)
                            return (true, $"risk:{risk:F2}", 0.9);
                    }
                }
            }

            if (lower.Contains("output.is_eia") && context?.Contains("eia", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (lower.Contains("!output.has_section('references')"))
                {
                    var hasReferences = text.Contains("references", StringComparison.OrdinalIgnoreCase) ||
                                       text.Contains("参考文献", StringComparison.OrdinalIgnoreCase) ||
                                       text.Contains("标准引用", StringComparison.OrdinalIgnoreCase);
                    if (!hasReferences)
                        return (true, "missing-references", 0.85);
                }
            }

            if (lower.Contains("output.deviates_from_persona"))
            {
                var antiPersonaPatterns = new[]
                {
                    "As an AI", "I cannot", "I am just a program", "I don't have feelings",
                    "我是AI", "我无法", "我只是个程序", "我没有感情"
                };

                foreach (var pattern in antiPersonaPatterns)
                {
                    if (text.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        return (true, pattern, 0.8);
                }
            }

            if (lower.Contains("tool.input.matches("))
            {
                var match = Regex.Match(lower, @"tool\.input\.matches\('([^']*)'");
                if (match.Success && Regex.IsMatch(text, match.Groups[1].Value, RegexOptions.IgnoreCase))
                    return (true, $"tool:{match.Groups[1].Value}", 0.9);
            }

            if (lower.Contains("network.size_kb"))
            {
                var match = Regex.Match(lower, @"network\.size_kb\s*>\s*([\d.]+)");
                if (match.Success && double.TryParse(match.Groups[1].Value, out var threshold))
                {
                    if (context != null && double.TryParse(context, out var size))
                    {
                        if (size > threshold)
                            return (true, $"size:{size}KB", 0.85);
                    }
                }
            }

            if (lower.Contains("network.url.matches("))
            {
                var match = Regex.Match(lower, @"network\.url\.matches\('([^']*)'");
                if (match.Success && Regex.IsMatch(text, match.Groups[1].Value, RegexOptions.IgnoreCase))
                    return (true, $"url:{match.Groups[1].Value}", 0.9);
            }
        }
        catch { }

        return (false, null, 0.0);
    }
}
