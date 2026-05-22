using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using LTAI.Planning.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Planning.Session;

public sealed partial class TerminalCompressor
{
    private const int SmallOutputThresholdDivisor = 4;

    private readonly int _maxChars;
    private readonly int _maxLines;
    private readonly CompressorStats _stats;
    private ILogger _logger;
    private readonly Random _rng;
    private GlobalRulePool? _rulePool;

    private static readonly Lazy<TerminalCompressor> _instance = new(() => new TerminalCompressor());
    public static TerminalCompressor Instance => _instance.Value;

    private TerminalCompressor(int maxChars = 3000, int maxLines = 200, ILogger? logger = null, GlobalRulePool? rulePool = null)
    {
        _maxChars = maxChars;
        _maxLines = maxLines;
        _stats = new CompressorStats();
        _logger = logger ?? NullLogger.Instance;
        _rng = new Random();
        _rulePool = rulePool;
    }

    public static void Configure(int maxChars = 3000, int maxLines = 200, ILogger? logger = null, GlobalRulePool? rulePool = null)
    {
        var field = typeof(TerminalCompressor)
            .GetField("_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (field?.GetValue(null) is Lazy<TerminalCompressor> oldLazy)
        {
            typeof(Lazy<TerminalCompressor>)
                .GetField("m_valueFactory", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(oldLazy, () => new TerminalCompressor(maxChars, maxLines, logger, rulePool));
            typeof(Lazy<TerminalCompressor>)
                .GetField("_value", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(oldLazy, null);
        }
    }

    public void SetLogger(ILogger logger)
    {
        _logger = logger;
    }

    public void SetRulePool(GlobalRulePool rulePool)
    {
        _rulePool = rulePool;
    }

    public CompressResult Compress(string output, string command = "", string namespace_ = "", string? context = null)
    {
        _stats.TotalCalls++;
        _stats.TotalInputChars += output.Length;

        if (string.IsNullOrEmpty(output))
        {
            _stats.PassThroughs++;
            return new CompressResult
            {
                Original = output,
                Compressed = output,
                OriginalChars = output.Length,
                CompressedChars = output.Length,
                Method = "empty"
            };
        }

        if (output.Length <= _maxChars / SmallOutputThresholdDivisor)
        {
            _stats.PassThroughs++;
            _stats.TotalOutputChars += output.Length;
            return new CompressResult
            {
                Original = output,
                Compressed = output,
                OriginalChars = output.Length,
                CompressedChars = output.Length,
                Method = "pass_through"
            };
        }

        if (string.IsNullOrEmpty(namespace_) && !string.IsNullOrEmpty(command))
        {
            namespace_ = DetectNamespace(command, output);
        }
        else if (string.IsNullOrEmpty(namespace_))
        {
            namespace_ = DetectNamespace("", output);
        }

        var (compressed, rulesApplied) = ApplyRules(output, namespace_, context, _rulePool);

        if (rulesApplied.Count > 0)
        {
            _stats.TotalOutputChars += compressed.Length;
            return new CompressResult
            {
                Original = output,
                Compressed = compressed,
                RulesApplied = rulesApplied,
                OriginalChars = output.Length,
                CompressedChars = compressed.Length,
                Method = "rules"
            };
        }

        _stats.FallbackTruncations++;
        var truncated = SmartTruncate(output);
        _stats.TotalOutputChars += truncated.Length;
        return new CompressResult
        {
            Original = output,
            Compressed = truncated,
            OriginalChars = output.Length,
            CompressedChars = truncated.Length,
            Method = "smart_truncate"
        };
    }

    public string DetectNamespace(string command, string output)
    {
        var combined = (command + "\n" + output).ToLowerInvariant();

        if (_gitPatterns().IsMatch(combined))
            return "git";

        if (_packagePatterns().IsMatch(combined))
            return "npm/nuget/dotnet";
        if (_testPatterns().IsMatch(combined))
            return "test";
        if (_dockerPatterns().IsMatch(combined))
            return "docker";
        if (_buildPatterns().IsMatch(combined))
            return "build";
        if (_errorPatterns().IsMatch(combined))
            return "error";

        return "general";
    }

    public (string text, List<string> rulesApplied) ApplyRules(
        string output, string namespace_, string? context, GlobalRulePool? rulePool)
    {
        var rulesApplied = new List<string>();
        if (rulePool == null)
            return (output, rulesApplied);

        var rules = rulePool.GetRulesForNamespace(namespace_)
            .OrderByDescending(r => r.Priority)
            .ToList();

        var currentText = output;
        foreach (var rule in rules)
        {
            if (!string.IsNullOrEmpty(rule.MatchContext) && context != null &&
                !context.Contains(rule.MatchContext, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var result = ApplyRuleAction(currentText, rule, out var changed);
                if (changed)
                {
                    currentText = result;
                    rulesApplied.Add(rule.Name);
                    _stats.RulesApplied++;
                    rule.HitCount++;
                    rule.LastHit = DateTime.UtcNow;
                    rulePool.RecordHit(rule.Id);
                }
                else
                {
                    rule.FalsePositiveCount++;
                    rulePool.RecordMiss(rule.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Rule {RuleName} failed to apply", rule.Name);
            }
        }

        return (currentText, rulesApplied);
    }

    private static string ApplyRuleAction(string text, CompressionRule rule, out bool changed)
    {
        changed = false;

        switch (rule.Action)
        {
            case RuleAction.PassThrough:
                return text;

            case RuleAction.TruncateTail:
                if (rule.TruncateLines > 0)
                {
                    var lines = text.Split('\n');
                    if (lines.Length > rule.TruncateLines)
                    {
                        changed = true;
                        return string.Join('\n', lines.Take(rule.TruncateLines));
                    }
                }
                if (rule.TruncateChars > 0 && text.Length > rule.TruncateChars)
                {
                    changed = true;
                    return text[..rule.TruncateChars];
                }
                return text;

            case RuleAction.ExtractPattern:
                if (!string.IsNullOrEmpty(rule.ExtractRegex))
                {
                    var matches = Regex.Matches(text, rule.ExtractRegex, RegexOptions.Multiline);
                    if (matches.Count > 0)
                    {
                        changed = true;
                        return string.Join('\n', matches.Select(m => m.Value));
                    }
                }
                return text;

            case RuleAction.Remove:
                if (!string.IsNullOrEmpty(rule.MatchPattern))
                {
                    var before = text.Length;
                    var result = Regex.Replace(text, rule.MatchPattern, "", RegexOptions.Multiline);
                    if (result.Length < before)
                    {
                        changed = true;
                        return Regex.Replace(result, @"\n{3,}", "\n\n");
                    }
                }
                return text;

            case RuleAction.Replace:
                if (!string.IsNullOrEmpty(rule.ReplacePattern) && rule.ReplaceWith != null)
                {
                    var before = text;
                    var result = Regex.Replace(text, rule.ReplacePattern, rule.ReplaceWith, RegexOptions.Multiline);
                    if (result != before)
                    {
                        changed = true;
                        return result;
                    }
                }
                return text;

            case RuleAction.Condense:
                var condensed = Regex.Replace(text, @"\n{3,}", "\n\n");
                var deduped = _dedupLinesRegex().Replace(condensed, "");
                if (deduped.Length < text.Length)
                {
                    changed = true;
                    return deduped;
                }
                return text;

            default:
                return text;
        }
    }

    public string SmartTruncate(string text)
    {
        var lines = text.Split('\n');

        if (lines.Length > _maxLines)
        {
            var headLines = (int)(_maxLines * 0.7);
            var tailLines = (int)(_maxLines * 0.3);
            var skipped = lines.Length - headLines - tailLines;

            if (tailLines <= 0)
            {
                return string.Join('\n', lines.Take(_maxLines)) +
                       $"\n... [{lines.Length - _maxLines} lines truncated] ...";
            }

            var head = string.Join('\n', lines.Take(headLines));
            var tail = string.Join('\n', lines.Skip(lines.Length - tailLines));
            return head + $"\n... [{skipped} lines truncated] ...\n" + tail;
        }

        if (text.Length > _maxChars)
        {
            var headChars = (int)(_maxChars * 0.8);
            var tailChars = (int)(_maxChars * 0.2);

            if (tailChars <= 0)
            {
                return text[.._maxChars] + $"\n... [{text.Length - _maxChars} chars truncated] ...";
            }

            var separator = $"\n... [{text.Length - headChars - tailChars} chars truncated] ...\n";
            return text[..headChars] + separator + text[^tailChars..];
        }

        return text;
    }

    public void ProposeRule(string output, string command)
    {
        var lines = output.Split('\n');
        var lineCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 5) continue;
            lineCounts.TryGetValue(trimmed, out var count);
            lineCounts[trimmed] = count + 1;
        }

        var repetitiveLines = lineCounts
            .Where(kv => kv.Value >= 5 && (double)kv.Value / lines.Length > 0.3)
            .ToList();

        if (repetitiveLines.Count > 0)
        {
            var ns = DetectNamespace(command, output);
            foreach (var (line, count) in repetitiveLines)
            {
                _logger.LogInformation(
                    "Proposed truncation rule for namespace={Ns}, pattern='{Pattern}', occurrences={Count}",
                    ns, line, count);
            }
        }
    }

    public Dictionary<string, object?> GetStats()
    {
        var poolStats = _rulePool?.GetStats();
        return new Dictionary<string, object?>
        {
            ["total_input_chars"] = _stats.TotalInputChars,
            ["total_output_chars"] = _stats.TotalOutputChars,
            ["total_calls"] = _stats.TotalCalls,
            ["rules_applied"] = _stats.RulesApplied,
            ["fallback_truncations"] = _stats.FallbackTruncations,
            ["pass_throughs"] = _stats.PassThroughs,
            ["compression_ratio"] = _stats.TotalInputChars > 0
                ? 1.0 - (double)_stats.TotalOutputChars / _stats.TotalInputChars
                : 0.0,
            ["rule_pool_stats"] = poolStats
        };
    }

    [GeneratedRegex(@"\bgit\b.*\b(diff|log|status|blame|checkout|commit|push|pull|fetch|merge|rebase|stash|reset|branch|add|rm|restore)\b",
        RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex _gitPatterns();

    [GeneratedRegex(@"\b(npm|nuget|dotnet)\b.*(\binstall\b|\brestore\b|\bpack\b|\bbuild\b|\brun\b|\btest\b|\bpublish\b)",
        RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex _packagePatterns();

    [GeneratedRegex(@"\b(test|pytest|xunit|nunit|mstest|jest|mocha|karma)\b|(passed|failed|skipped)\s*\d+|Tests?\s+(passed|failed)|Test run|test results",
        RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex _testPatterns();

    [GeneratedRegex(@"\bdocker\b.*\b(build|run|compose|push|pull|image|container|exec|logs|ps|stop|rm)\b",
        RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex _dockerPatterns();

    [GeneratedRegex(@"\b(msbuild|make|cmake|ninja|rake|grunt|gulp)\b|(?:\d+>\s*)?\w+\.(cs|cpp|c|java|py|ts|js|go|rs)\(\d+,\d+\)\s*:\s*(error|warning)",
        RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex _buildPatterns();

    [GeneratedRegex(@"\b(error|exception|fail|crash|traceback|stack\s*trace)\b|at\s+\w+\.\w+\.\w+\(.*\)\s+in\s+.*:\w+\s+\d+|^\s*(Error|Exception|Fatal|Critical)\s*:",
        RegexOptions.Multiline | RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex _errorPatterns();

    [GeneratedRegex(@"^(.+)$\n(\1\n)+", RegexOptions.Multiline)]
    private static partial Regex _dedupLinesRegex();
}
