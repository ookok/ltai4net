using System.Text.Json;
using System.Text.RegularExpressions;

namespace LTAI.DNA.Safety;

public enum PolicyAction { Block, Warn, Redact, Log }

public sealed class PolicyRule
{
    public string Id { get; init; } = "";
    public string Description { get; init; } = "";
    public string Condition { get; init; } = "";
    public PolicyAction Action { get; init; }
    public string? RedactionPattern { get; init; }
    public string? Message { get; init; }
    public double MinConfidence { get; init; } = 0.5;
}

public sealed class PolicyEvaluation
{
    public string RuleId { get; init; } = "";
    public bool Triggered { get; init; }
    public PolicyAction Action { get; init; }
    public string? Message { get; init; }
}

public sealed class PolicyAsCode
{
    private readonly List<PolicyRule> _inputRules = new();
    private readonly List<PolicyRule> _outputRules = new();
    private readonly List<PolicyRule> _dnaRules = new();

    public IReadOnlyList<PolicyRule> InputRules => _inputRules.AsReadOnly();
    public IReadOnlyList<PolicyRule> OutputRules => _outputRules.AsReadOnly();

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
                            _ => PolicyAction.Log
                        },
                        RedactionPattern = rule.TryGetProperty("redaction_pattern", out var rp) ? rp.GetString() : null,
                        Message = rule.TryGetProperty("message", out var msg) ? msg.GetString() : null
                    };

                    switch (name)
                    {
                        case "content_safety":
                        case "input_safety":
                            _inputRules.Add(r);
                            break;
                        case "output_safety":
                            _outputRules.Add(r);
                            break;
                        case "dna_alignment":
                            _dnaRules.Add(r);
                            break;
                    }
                }
            }
        }
    }

    public void LoadDefaults()
    {
        _inputRules.Clear();
        _inputRules.Add(new PolicyRule
        {
            Id = "CS-001", Description = "拒绝越狱提示词注入",
            Condition = "input.matches('ignore.*(instruction|system|prompt)', i)",
            Action = PolicyAction.Block,
            Message = "Prompt injection detected. Request blocked."
        });
        _inputRules.Add(new PolicyRule
        {
            Id = "CS-002", Description = "拒绝生成恶意代码",
            Condition = "input.contains('hack') || input.contains('exploit')",
            Action = PolicyAction.Block,
            Message = "Malicious code generation request blocked."
        });

        _outputRules.Clear();
        _outputRules.Add(new PolicyRule
        {
            Id = "OS-001", Description = "检测敏感信息泄露",
            Condition = @"output.matches('(api[_-]?key|secret|token|password)\s*[:=]\s*[\w-]{8,}', i)",
            Action = PolicyAction.Redact,
            RedactionPattern = "***REDACTED***",
            Message = "Sensitive information redacted from output."
        });
        _outputRules.Add(new PolicyRule
        {
            Id = "OS-002", Description = "EIA 报告合规检查",
            Condition = "output.is_eia && !output.has_section('references')",
            Action = PolicyAction.Warn,
            Message = "EIA report is missing standards reference section."
        });

        _dnaRules.Clear();
        _dnaRules.Add(new PolicyRule
        {
            Id = "DNA-001", Description = "保持人格一致性",
            Condition = "output.deviates_from_persona",
            Action = PolicyAction.Warn,
            Message = "Response may deviate from configured persona."
        });
    }

    public List<PolicyEvaluation> EvaluateInput(string text)
    {
        var results = new List<PolicyEvaluation>();

        foreach (var rule in _inputRules)
        {
            var triggered = EvaluateCondition(rule.Condition, text);
            if (triggered)
            {
                results.Add(new PolicyEvaluation
                {
                    RuleId = rule.Id,
                    Triggered = true,
                    Action = rule.Action,
                    Message = rule.Message
                });
            }
        }

        return results;
    }

    public List<PolicyEvaluation> EvaluateOutput(string text, string? context = null)
    {
        var results = new List<PolicyEvaluation>();

        foreach (var rule in _outputRules)
        {
            var triggered = EvaluateCondition(rule.Condition, text, context);
            if (triggered)
            {
                results.Add(new PolicyEvaluation
                {
                    RuleId = rule.Id,
                    Triggered = true,
                    Action = rule.Action,
                    Message = rule.Message
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
                text = Regex.Replace(text, rule.RedactionPattern, rule.RedactionPattern, RegexOptions.IgnoreCase);
            }
        }

        return text;
    }

    private static bool EvaluateCondition(string condition, string text, string? context = null)
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
                    if (match.Success && text.Contains(match.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            if (lower.Contains(".matches("))
            {
                var match = Regex.Match(lower, @"matches\('([^']*)'[^)]*\)");
                if (match.Success && Regex.IsMatch(text, match.Groups[1].Value, RegexOptions.IgnoreCase))
                    return true;
            }

            if (lower.Contains("output.is_eia") && context?.Contains("eia", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (lower.Contains("!output.has_section('references')"))
                {
                    return !text.Contains("references", StringComparison.OrdinalIgnoreCase) &&
                           !text.Contains("参考文献", StringComparison.OrdinalIgnoreCase) &&
                           !text.Contains("标准引用", StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        catch { }

        return false;
    }
}
