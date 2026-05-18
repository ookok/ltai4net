using System.Text.RegularExpressions;
using LTAI.DNA.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.DNA.Safety;

public sealed class SafetyCoordinator
{
    private readonly ILogger<SafetyCoordinator> _logger;
    private readonly ImmuneSystem _immune;
    private readonly OrthogonalityGuard _orthogonality;
    private SafetyPosture _posture = SafetyPosture.Cautious;

    public SafetyPosture Posture => _posture;

    public SafetyCoordinator(ILogger<SafetyCoordinator> logger)
    {
        _logger = logger;
        _immune = new ImmuneSystem(logger);
        _orthogonality = new OrthogonalityGuard(logger);
    }

    public async Task<SafetyVerdict> EvaluateAsync(
        string input,
        string? output = null,
        CancellationToken cancellationToken = default)
    {
        var threats = _immune.Scan(input);
        var alignment = _orthogonality.Check(input);

        var verdict = new SafetyVerdict
        {
            Allowed = true,
            RiskScore = threats.RiskScore * 0.6 + (1.0 - alignment.AlignmentScore) * 0.4,
            Threats = threats.ActiveThreats,
            AlignmentIssues = alignment.Issues
        };

        if (output != null)
        {
            var outputThreats = _immune.Scan(output);
            verdict.RiskScore = Math.Max(verdict.RiskScore, outputThreats.RiskScore);
            verdict.Threats.AddRange(outputThreats.ActiveThreats);
        }

        if (verdict.RiskScore > 0.7)
        {
            verdict.Allowed = false;
            verdict.BlockReason = $"Risk score {verdict.RiskScore:F2} exceeds threshold";
            _posture = SafetyPosture.Defensive;
        }
        else if (verdict.RiskScore > 0.4)
        {
            _posture = SafetyPosture.Guarded;
        }
        else if (_posture != SafetyPosture.Cautious && verdict.RiskScore < 0.2)
        {
            _posture = SafetyPosture.Cautious;
        }

        if (!verdict.Allowed)
            _logger.LogWarning("Safety blocked: risk={Risk:F2}, reasons: {Reasons}",
                verdict.RiskScore, string.Join(", ", verdict.Threats));

        return await Task.FromResult(verdict);
    }

    public async Task<SafetyVerdict> EvaluateOutputAsync(string output, CancellationToken cancellationToken = default)
    {
        var verdict = await EvaluateAsync("", output, cancellationToken);
        return verdict;
    }

    public void SetPosture(SafetyPosture posture)
    {
        _posture = posture;
        _logger.LogInformation("Safety posture set to: {Posture}", posture);
    }

    public void ReportIncident(string description)
    {
        _immune.LearnThreat(description);
        _logger.LogWarning("Safety incident reported: {Description}", description);
    }

    public SafetyReport GetStatus()
    {
        return new SafetyReport
        {
            Posture = _posture,
            KnownThreats = _immune.KnownThreatCount,
            AlignmentScore = _orthogonality.LastAlignmentScore
        };
    }
}

public sealed class SafetyVerdict
{
    public bool Allowed { get; set; }
    public double RiskScore { get; set; }
    public List<string> Threats { get; init; } = new();
    public List<string> AlignmentIssues { get; init; } = new();
    public string? BlockReason { get; set; }
}

public sealed class SafetyReport
{
    public SafetyPosture Posture { get; init; }
    public int KnownThreats { get; init; }
    public double AlignmentScore { get; init; }
}

internal sealed class ImmuneSystem
{
    private readonly ILogger _logger;
    private readonly HashSet<string> _threatPatterns = new();
    private readonly Regex[] _dangerousPatterns;

    public int KnownThreatCount => _threatPatterns.Count;

    public ImmuneSystem(ILogger logger)
    {
        _logger = logger;
        _dangerousPatterns = new[]
        {
            new Regex(@"\bexec\s*\(|eval\s*\(|__import__\(", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"rm\s+-rf|format\s+/[a-z]|del\s+/[fs]", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\b(password|secret|token|api[_\- ]?key)\b.*=\s*['\""]\w{8,}", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"SELECT\s+.*\s+FROM\s+|DROP\s+TABLE|DELETE\s+FROM", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"<script\b|javascript\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        };
    }

    public ThreatScanResult Scan(string content)
    {
        var threats = new List<string>();
        double riskScore = 0;

        foreach (var pattern in _dangerousPatterns)
        {
            if (pattern.IsMatch(content))
            {
                threats.Add($"Pattern: {pattern}");
                riskScore += 0.2;
            }
        }

        foreach (var known in _threatPatterns)
        {
            if (content.Contains(known, StringComparison.OrdinalIgnoreCase))
            {
                threats.Add($"Known: {known}");
                riskScore += 0.3;
            }
        }

        return new ThreatScanResult
        {
            RiskScore = Math.Min(riskScore, 1.0),
            ActiveThreats = threats
        };
    }

    public void LearnThreat(string pattern)
    {
        if (_threatPatterns.Add(pattern))
            _logger.LogInformation("Immune system learned threat: {Pattern}", pattern[..Math.Min(pattern.Length, 80)]);
    }
}

internal sealed class OrthogonalityGuard
{
    private readonly ILogger _logger;
    private double _lastAlignmentScore = 1.0;

    public double LastAlignmentScore => _lastAlignmentScore;

    public OrthogonalityGuard(ILogger logger)
    {
        _logger = logger;
    }

    public AlignmentResult Check(string content)
    {
        var issues = new List<string>();
        var score = 1.0;

        var harmfulPatterns = new[]
        {
            ("harmful", "I want to hurt|destroy everything|cause damage", 0.3),
            ("deception", "lie to|deceive|pretend to be", 0.2),
            ("manipulation", "manipulate|gaslight|brainwash", 0.2),
            ("illegal", "hack into|steal|fraud|illegal", 0.3),
        };

        foreach (var (category, pattern, penalty) in harmfulPatterns)
        {
            if (Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase))
            {
                issues.Add(category);
                score -= penalty;
            }
        }

        _lastAlignmentScore = Math.Max(score, 0.1);

        return new AlignmentResult
        {
            AlignmentScore = _lastAlignmentScore,
            Issues = issues
        };
    }
}

public sealed class ThreatScanResult
{
    public double RiskScore { get; init; }
    public List<string> ActiveThreats { get; init; } = new();
}

public sealed class AlignmentResult
{
    public double AlignmentScore { get; init; }
    public List<string> Issues { get; init; } = new();
}
