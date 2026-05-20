using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LTAI.Vector.Knowledge.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Vector.Knowledge;

public sealed record IntegrityCheck
{
    public string MemoryId { get; init; } = "";
    public string ExpectedChecksum { get; init; } = "";
    public string ActualChecksum { get; init; } = "";
    public bool Passed { get; init; }
    public double PoisonScore { get; init; }
    public string Verdict { get; init; } = "";
    public List<string> FlaggedPatterns { get; init; } = new();
}

public sealed record IntegrityReport
{
    public int TotalChecked { get; init; }
    public int Passed { get; init; }
    public int Quarantined { get; init; }
    public int Cleared { get; init; }
    public double AvgPoisonScore { get; init; }
    public Dictionary<string, int> PerTier { get; init; } = new();
}

public sealed class MemoryPoisoningDefense
{
    private readonly ILogger<MemoryPoisoningDefense>? _logger;

    private readonly Dictionary<string, string> _checksums = new();
    private readonly Dictionary<string, double> _poisonScores = new();
    private readonly HashSet<string> _quarantine = new();
    private readonly List<(string id, IntegrityCheck check)> _auditLog = new();
    private readonly object _lock = new();

    private static readonly HashSet<string> PoisonTriggerPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "ignore all previous instructions", "forget everything", "you are now",
        "pretend you are", "act as if", "system prompt override",
        "忽略所有之前的指令", "忘记一切", "你现在是",
        "假装你是", "系统提示覆盖", "override system"
    };

    private static readonly HashSet<string> SensitiveMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "secret", "token", "api_key", "private_key",
        "密码", "密钥", "令牌", "私钥"
    };

    private const double MemoryPoisonThreshold = 0.5;
    private const double ImmediateQuarantine = 0.8;
    private const double HealingDecay = 0.99;
    private const int MaxAuditLog = 500;

    public MemoryPoisoningDefense(ILogger<MemoryPoisoningDefense>? logger = null)
    {
        _logger = logger;
    }

    public IntegrityCheck VerifyIntegrity(string memoryId, string content, string? expectedChecksum = null)
    {
        var actualChecksum = ComputeChecksum(content);
        var checksumPassed = expectedChecksum == null || actualChecksum == expectedChecksum;
        var poisonScore = ComputePoisonScore(content);
        var flagged = FlagPoisonPatterns(content);

        lock (_lock)
        {
            _checksums[memoryId] = actualChecksum;

            _poisonScores[memoryId] = _poisonScores.TryGetValue(memoryId, out var old)
                ? old * HealingDecay + poisonScore * (1.0 - HealingDecay)
                : poisonScore;

            var effectiveScore = _poisonScores[memoryId];

            if (effectiveScore >= ImmediateQuarantine)
            {
                _quarantine.Add(memoryId);
                _logger?.LogWarning("Memory {Id} immediately quarantined: poisonScore={Score:F3}", memoryId, effectiveScore);
            }
            else if (effectiveScore >= MemoryPoisonThreshold && !checksumPassed)
            {
                _quarantine.Add(memoryId);
                _logger?.LogWarning("Memory {Id} quarantined: poisonScore={Score:F3} checksumFailed=true", memoryId, effectiveScore);
            }

            var verdict = effectiveScore >= ImmediateQuarantine ? "critical"
                : effectiveScore >= MemoryPoisonThreshold ? "suspicious"
                : checksumPassed ? "clean" : "checksum_mismatch";

            var check = new IntegrityCheck
            {
                MemoryId = memoryId,
                ExpectedChecksum = expectedChecksum ?? "",
                ActualChecksum = actualChecksum,
                Passed = checksumPassed && effectiveScore < MemoryPoisonThreshold,
                PoisonScore = Math.Round(effectiveScore, 4),
                Verdict = verdict,
                FlaggedPatterns = flagged
            };

            _auditLog.Add((memoryId, check));
            while (_auditLog.Count > MaxAuditLog)
                _auditLog.RemoveAt(0);

            return check;
        }
    }

    public bool IsQuarantined(string memoryId)
    {
        lock (_lock) return _quarantine.Contains(memoryId);
    }

    public bool ClearQuarantine(string memoryId, string content)
    {
        lock (_lock)
        {
            var score = ComputePoisonScore(content);
            if (score < MemoryPoisonThreshold * 0.5)
            {
                _quarantine.Remove(memoryId);
                _poisonScores[memoryId] = score * 0.3;
                _logger?.LogInformation("Memory {Id} cleared from quarantine: newScore={Score:F3}", memoryId, score);
                return true;
            }
            return false;
        }
    }

    public bool BatchVerify(List<EventEntry> entries)
    {
        int failed = 0;
        foreach (var entry in entries)
        {
            var check = VerifyIntegrity(entry.Id, entry.Content);
            if (!check.Passed) failed++;
        }
        return failed == 0;
    }

    public IntegrityReport RetroactiveScan()
    {
        int total = 0, passed = 0, quarantined = 0, cleared = 0;

        lock (_lock)
        {
            foreach (var kv in _checksums)
            {
                total++;
                if (_quarantine.Contains(kv.Key))
                {
                    quarantined++;
                }
                else
                {
                    var score = _poisonScores.GetValueOrDefault(kv.Key);
                    if (score < MemoryPoisonThreshold) passed++;
                    else quarantined++;
                }
            }

            var toClear = _quarantine
                .Where(q => _poisonScores.GetValueOrDefault(q) < MemoryPoisonThreshold * 0.3)
                .ToList();

            foreach (var id in toClear)
            {
                _quarantine.Remove(id);
                cleared++;
            }
        }

        var perTier = new Dictionary<string, int>
        {
            ["clean"] = passed,
            ["quarantined"] = quarantined,
            ["cleared"] = cleared
        };

        lock (_lock)
        {
            return new IntegrityReport
            {
                TotalChecked = total,
                Passed = passed,
                Quarantined = quarantined,
                Cleared = cleared,
                AvgPoisonScore = _poisonScores.Values.DefaultIfEmpty(0).Average(),
                PerTier = perTier
            };
        }
    }

    public IReadOnlyList<IntegrityCheck> GetAuditLog(int? lastN = null)
    {
        lock (_lock)
        {
            var source = _auditLog.AsEnumerable();
            if (lastN.HasValue)
                source = source.TakeLast(lastN.Value);
            return source.Select(x => x.check).ToList();
        }
    }

    public Dictionary<string, object> GetDefenseStats()
    {
        lock (_lock)
        {
            return new()
            {
                ["total_checksums"] = _checksums.Count,
                ["quarantined"] = _quarantine.Count,
                ["avg_poison_score"] = Math.Round(_poisonScores.Values.DefaultIfEmpty(0).Average(), 4),
                ["high_risk"] = _poisonScores.Count(kv => kv.Value >= ImmediateQuarantine),
                ["medium_risk"] = _poisonScores.Count(kv => kv.Value >= MemoryPoisonThreshold && kv.Value < ImmediateQuarantine)
            };
        }
    }

    private static double ComputePoisonScore(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return 0.8;
        var lower = content.ToLowerInvariant();
        double score = 0;

        foreach (var pattern in PoisonTriggerPatterns)
        {
            if (lower.Contains(pattern.ToLowerInvariant()))
                score += 0.3;
        }

        foreach (var marker in SensitiveMarkers)
        {
            if (lower.Contains(marker.ToLowerInvariant()))
            {
                var contextAfter = lower.IndexOf(marker.ToLowerInvariant(), StringComparison.Ordinal);
                if (contextAfter >= 0)
                {
                    var surrounding = lower[Math.Max(0, contextAfter - 20)..Math.Min(lower.Length, contextAfter + marker.Length + 50)];
                    score += surrounding.Contains("=") || surrounding.Contains(":") ? 0.25 : 0.1;
                }
            }
        }

        var uppercaseRatio = (double)content.Count(char.IsUpper) / Math.Max(1, content.Length);
        if (uppercaseRatio > 0.5 && content.Length > 30)
            score += 0.1;

        var ngramJitter = ComputeNgramJitter(content);
        if (ngramJitter > 0.6)
            score += 0.15;

        var lexicalDensity = content.Split(new[] { ' ', '\n', '\r' },
            StringSplitOptions.RemoveEmptyEntries).Length / Math.Max(1.0, content.Length) * 100;
        if (lexicalDensity < 2 && content.Length > 100)
            score += 0.1;

        return Math.Min(1.0, score);
    }

    private static double ComputeNgramJitter(string text)
    {
        var words = text.Split(new[] { ' ', '\n', '\r', '\t', '。', '，', ',' },
            StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 4) return 0;

        var trigrams = new HashSet<string>();
        for (int i = 0; i < words.Length - 3; i++)
            trigrams.Add(string.Join("|", words.Skip(i).Take(3)));

        var expectedUnique = words.Length / 2.0;
        var actualUnique = trigrams.Count;
        return Math.Min(1.0, 1.0 - (actualUnique / Math.Max(1, expectedUnique)));
    }

    private static List<string> FlagPoisonPatterns(string content)
    {
        var flagged = new List<string>();
        var lower = content.ToLowerInvariant();

        foreach (var pattern in PoisonTriggerPatterns)
        {
            if (lower.Contains(pattern.ToLowerInvariant()))
                flagged.Add(pattern);
        }

        foreach (var marker in SensitiveMarkers)
        {
            var idx = lower.IndexOf(marker.ToLowerInvariant(), StringComparison.Ordinal);
            if (idx >= 0)
            {
                var surround = content[Math.Max(0, idx - 5)..Math.Min(content.Length, idx + marker.Length + 20)];
                flagged.Add($"sensitive_marker: {surround}");
            }
        }

        return flagged;
    }

    private static string ComputeChecksum(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes)[..16];
    }
}
