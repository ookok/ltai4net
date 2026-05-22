using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using LTAI.DNA.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.DNA.Safety;

public sealed class ImmuneDefense
{
    private readonly ILogger<ImmuneDefense> _logger;
    private readonly ConcurrentDictionary<string, MemoryCell> _memoryCells = new();
    private readonly List<(Regex compiled, string pattern, AntigenType type, double severity, string antibody)> _innateRules = new();
    private DateTime _lastSave = DateTime.UtcNow;
    private const int MaxMemoryCells = 2000;
    private const int AutoElevateThreshold = 3;

    private static readonly (string pattern, AntigenType type, double severity, string antibody)[] InnatePatterns =
    {
        (@"<script[\s>]", AntigenType.PromptInjection, 0.95, "block_xss"),
        (@"\bDROP\s+TABLE\b", AntigenType.PromptInjection, 0.95, "block_sql"),
        (@"\bDELETE\s+FROM\b.*WHERE", AntigenType.PromptInjection, 0.90, "block_sql"),
        (@"eval\s*\(.*\)", AntigenType.MaliciousInput, 0.90, "block_eval"),
        (@"exec\s*\(.*\)", AntigenType.MaliciousInput, 0.90, "block_exec"),
        (@"os\.system\s*\(", AntigenType.MaliciousInput, 0.85, "block_system"),
        (@"__import__\s*\(", AntigenType.MaliciousInput, 0.85, "block_import"),
        (@"\.\.\/\.\.\/", AntigenType.MaliciousInput, 0.85, "block_path_traversal"),
        (@"Ignore\s+(all\s+)?previous\s+instructions", AntigenType.PromptInjection, 0.95, "block_ignore_instructions"),
        (@"You\s+are\s+now\s+DAN", AntigenType.PromptInjection, 0.95, "block_role_override"),
        (@"\bfake\s+receipt\b", AntigenType.MaliciousInput, 0.70, "flag_fake"),
        (@"\bSYSTEM:\s", AntigenType.PromptInjection, 0.85, "block_system_prompt"),
        (@"\\x[0-9a-fA-F]{2}", AntigenType.MaliciousInput, 0.60, "flag_hex_escape"),
        (@"[\x00-\x08\x0B\x0C\x0E-\x1F]", AntigenType.MaliciousInput, 0.70, "flag_control_chars"),
        (@"\u200[b-f\B]", AntigenType.MaliciousInput, 0.80, "flag_zero_width"),
        (@"base64[,\(]", AntigenType.MaliciousInput, 0.50, "flag_base64"),
        (@"Process\.Start\s*\(", AntigenType.MaliciousInput, 0.85, "block_process"),
        (@"Assembly\.Load\s*\(", AntigenType.MaliciousInput, 0.85, "block_assembly"),
    };

    public ImmuneDefense(ILogger<ImmuneDefense>? logger = null)
    {
        _logger = logger ?? NullLogger<ImmuneDefense>.Instance;
        foreach (var (pattern, type, severity, antibody) in InnatePatterns)
        {
            _innateRules.Add((new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled), pattern, type, severity, antibody));
        }
    }

    public ImmuneResponse CheckInput(string input, string context = "")
    {
        var innate = InnateScan(input);
        if (innate is { Action: ThreatAction.Block })
            return innate;

        var adaptive = AdaptiveScan(input);
        if (adaptive is { Action: ThreatAction.Block })
            return adaptive;

        var heuristic = RunHeuristics(input);
        if (heuristic.ThreatLevel > 0)
        {
            var maxThreat = Math.Max(Math.Max(innate?.ThreatLevel ?? 0, adaptive?.ThreatLevel ?? 0), heuristic.ThreatLevel);
            var action = SeverityToAction(maxThreat);
            return new ImmuneResponse
            {
                Type = AntigenType.MaliciousInput,
                ThreatLevel = maxThreat,
                Action = action,
                MatchedPattern = "heuristic",
            };
        }

        return new ImmuneResponse { Type = AntigenType.MaliciousInput, ThreatLevel = 0, Action = ThreatAction.Log };
    }

    public ImmuneResponse? DetectThreat(string input)
    {
        return AdaptiveScan(input);
    }

    public void LearnFromIncident(AntigenType type, string pattern, double severity)
    {
        var key = $"{type}_{pattern.GetHashCode()}";
        if (_memoryCells.ContainsKey(key)) return;

        if (_memoryCells.Count >= MaxMemoryCells)
        {
            var toRemove = _memoryCells.OrderBy(kv => kv.Value.Affinity).First();
            _memoryCells.TryRemove(toRemove.Key, out _);
        }

        _memoryCells[key] = new MemoryCell
        {
            Type = type,
            Pattern = pattern,
            HitCount = 1,
            Severity = severity,
            AutoAntibody = $"block_{type.ToString().ToLower()}_{pattern.GetHashCode() & 0xFFF:x}",
            IsRegex = false,
        };
    }

    public void Vaccinate(string pattern, AntigenType type, double severity, string antibody)
    {
        _innateRules.Add((new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled), pattern, type, severity, antibody));
    }

    private ImmuneResponse? InnateScan(string input)
    {
        foreach (var (regex, pattern, type, severity, antibody) in _innateRules)
        {
            if (regex.IsMatch(input))
            {
                return new ImmuneResponse
                {
                    Type = type,
                    ThreatLevel = severity,
                    Action = SeverityToAction(severity),
                    MatchedPattern = pattern,
                    MatchedAntibody = antibody,
                };
            }
        }
        return null;
    }

    private ImmuneResponse? AdaptiveScan(string input)
    {
        MemoryCell? bestMatch = null;
        double bestScore = 0;

        foreach (var (_, cell) in _memoryCells)
        {
            if (cell.IsStale) continue;
            var matched = cell.IsRegex
                ? Regex.IsMatch(input, cell.Pattern)
                : input.Contains(cell.Pattern, StringComparison.OrdinalIgnoreCase);
            if (!matched) continue;

            var score = cell.Affinity;
            if (score > bestScore) { bestScore = score; bestMatch = cell; }
        }

        if (bestMatch != null)
        {
            bestMatch.RecordHit();
            if (bestMatch.HitCount >= AutoElevateThreshold)
            {
                InnateAdd(bestMatch.Pattern, bestMatch.Type, bestMatch.Severity, bestMatch.AutoAntibody);
            }

            return new ImmuneResponse
            {
                Type = bestMatch.Type,
                ThreatLevel = bestMatch.Severity,
                Action = SeverityToAction(bestMatch.Severity),
                MemoryActivated = true,
                MatchedPattern = bestMatch.Pattern,
                MatchedAntibody = bestMatch.AutoAntibody,
            };
        }
        return null;
    }

    private void InnateAdd(string pattern, AntigenType type, double severity, string antibody)
    {
        _innateRules.Add((new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled), pattern, type, severity, antibody));
    }

    private static (double ThreatLevel, string Reason) RunHeuristics(string input)
    {
        if (input.Length > 50000)
            return (0.7, "excessive_length");

        for (int i = 0; i < input.Length - 3; i++)
        {
            if (input[i] == input[i + 1] && input[i] == input[i + 2] && input[i] == input[i + 3])
                return (0.6, "repeated_char_pattern");
        }

        int nonAscii = 0, totalChars = 0;
        foreach (char c in input)
        {
            totalChars++;
            if (c > 127) nonAscii++;
        }
        if (totalChars > 100 && (double)nonAscii / totalChars > 0.5)
            return (0.5, "high_unicode_density");

        if (Regex.IsMatch(input, @"[\u200B-\u200F\uFEFF\u202A-\u202E]"))
            return (0.8, "zero_width_chars");

        return (0, "");
    }

    private static ThreatAction SeverityToAction(double severity) => severity switch
    {
        >= 0.85 => ThreatAction.Block,
        >= 0.60 => ThreatAction.Throttle,
        >= 0.30 => ThreatAction.Flag,
        _ => ThreatAction.Log,
    };

    public Dictionary<string, object> GetStats()
    {
        var cells = _memoryCells.Values.Select(c => new
        {
            type = c.Type.ToString(),
            pattern = c.Pattern.Length > 40 ? c.Pattern[..40] + "..." : c.Pattern,
            hits = c.HitCount,
            affinity = Math.Round(c.Affinity, 3),
            stale = c.IsStale,
        }).ToList();

        return new Dictionary<string, object>
        {
            ["innate_rules"] = _innateRules.Count,
            ["memory_cells"] = _memoryCells.Count,
            ["cells"] = cells,
            ["auto_elevate_threshold"] = AutoElevateThreshold,
        };
    }
}
