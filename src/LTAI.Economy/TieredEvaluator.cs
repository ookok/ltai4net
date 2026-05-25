using System.Collections.Concurrent;
using System.Diagnostics;

namespace LTAI.Economy;

public sealed record EvaluationResult(
    string CandidateId,
    double CorrectnessScore,
    double SecurityScore,
    double LatencyMs,
    double MemoryMb,
    double ComputeUtilization,
    double FitnessScore,
    Dictionary<string, double> ProfilingMetrics,
    List<string> Warnings,
    bool Passed)
{
    public bool PassedCorrectness => CorrectnessScore >= 1.0;
    public bool PassedSecurity => SecurityScore >= 1.0;
}

public sealed record SecurityConstraint(
    string Name,
    string Description,
    Func<EvolutionCandidate, bool> Check,
    double Weight = 1.0)
{
    public double ComputeScore(EvolutionCandidate candidate)
    {
        try { return Check(candidate) ? 1.0 : 0.0; }
        catch { return 0.0; }
    }
}

public sealed class TieredEvaluator
{
    private readonly HardwareProfiler _profiler;
    private readonly List<SecurityConstraint> _securityConstraints = new();
    private readonly ConcurrentDictionary<string, EvaluationResult> _results = new();
    private readonly int _maxRetries = 3;

    public event Action<EvolutionCandidate, EvaluationResult>? OnEvaluationComplete;
    public event Action<EvolutionCandidate, string>? OnRejection;

    public TieredEvaluator(HardwareProfiler profiler)
    {
        _profiler = profiler;
    }

    public void AddSecurityConstraint(SecurityConstraint constraint)
    {
        _securityConstraints.Add(constraint);
    }

    public async Task<EvaluationResult> EvaluateAsync(
        EvolutionCandidate candidate,
        CancellationToken ct = default)
    {
        var warnings = new List<string>();

        var correctnessScore = await EvaluateCorrectnessAsync(candidate, warnings, ct).ConfigureAwait(false);

        if (correctnessScore < 1.0)
        {
            OnRejection?.Invoke(candidate, $"Correctness failed: score={correctnessScore:F2}");
            return CreateRejectedResult(candidate, correctnessScore, 0, warnings);
        }

        var securityScore = EvaluateSecurity(candidate, warnings);

        if (securityScore < 1.0)
        {
            OnRejection?.Invoke(candidate, $"Security failed: score={securityScore:F2}");
            return CreateRejectedResult(candidate, correctnessScore, securityScore, warnings);
        }

        var (latencyMs, profilingMetrics) = await _profiler.ProfileAsync(candidate, ct).ConfigureAwait(false);

        var memMb = profilingMetrics.GetValueOrDefault("memory_mb", 0);
        var computeUtil = profilingMetrics.GetValueOrDefault("compute_util", 0);

        if (latencyMs > 10000)
        {
            warnings.Add($"High latency detected: {latencyMs:F0}ms > 10s threshold");
        }

        if (computeUtil < 0.1)
        {
            warnings.Add($"Low compute utilization: {computeUtil:P1}");
        }

        var fitnessScore = ComputeFitnessScore(correctnessScore, securityScore, latencyMs, computeUtil);

        var result = new EvaluationResult(
            CandidateId: candidate.Id,
            CorrectnessScore: correctnessScore,
            SecurityScore: securityScore,
            LatencyMs: latencyMs,
            MemoryMb: memMb,
            ComputeUtilization: computeUtil,
            FitnessScore: fitnessScore,
            ProfilingMetrics: profilingMetrics,
            Warnings: warnings,
            Passed: true);

        _results[candidate.Id] = result;
        OnEvaluationComplete?.Invoke(candidate, result);
        return result;
    }

    public async Task<List<EvaluationResult>> EvaluateBatchAsync(
        List<EvolutionCandidate> candidates,
        int maxConcurrency = 10,
        CancellationToken ct = default)
    {
        var results = new ConcurrentBag<EvaluationResult>();
        var semaphore = new SemaphoreSlim(maxConcurrency);

        var tasks = candidates.Select(async candidate =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = await EvaluateAsync(candidate, ct).ConfigureAwait(false);
                results.Add(result);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.ToList();
    }

    public EvaluationResult? GetResult(string candidateId)
    {
        _results.TryGetValue(candidateId, out var result);
        return result;
    }

    public Dictionary<string, double> GetStats()
    {
        var all = _results.Values.ToList();
        if (all.Count == 0)
            return new Dictionary<string, double> { ["no_results"] = 0 };

        return new Dictionary<string, double>
        {
            ["total_evaluated"] = all.Count,
            ["passed_rate"] = (double)all.Count(r => r.Passed) / all.Count,
            ["avg_correctness"] = all.Average(r => r.CorrectnessScore),
            ["avg_security"] = all.Average(r => r.SecurityScore),
            ["avg_latency_ms"] = all.Average(r => r.LatencyMs),
            ["min_latency_ms"] = all.Min(r => r.LatencyMs),
            ["avg_compute_util"] = all.Average(r => r.ComputeUtilization),
            ["avg_fitness"] = all.Average(r => r.FitnessScore)
        };
    }

    private async Task<double> EvaluateCorrectnessAsync(
        EvolutionCandidate candidate,
        List<string> warnings,
        CancellationToken ct)
    {
        var retries = 0;
        while (retries < _maxRetries)
        {
            try
            {
                var score = await CheckCompilationAsync(candidate.Code, ct).ConfigureAwait(false);
                if (score >= 1.0)
                {
                    score += await CheckRuntimeBehaviorAsync(candidate, ct).ConfigureAwait(false);
                    return Math.Min(1.0, score / 2.0);
                }
                return score;
            }
            catch
            {
                retries++;
                if (retries >= _maxRetries)
                {
                    warnings.Add($"Failed after {_maxRetries} correctness check retries");
                    return 0;
                }
                await Task.Delay(100 * retries, ct).ConfigureAwait(false);
            }
        }
        return 0;
    }

    private async Task<double> CheckCompilationAsync(string code, CancellationToken ct)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(code))
        {
            errors.Add("Empty code");
            return 0;
        }

        if (code.Length < 10)
        {
            errors.Add("Code too short (likely fragment)");
            return 0.2;
        }

        var hasFunctionDef = code.Contains("def ") || code.Contains("async def ") ||
                            code.Contains("class ") || code.Contains("function ") ||
                            code.Contains("@");
        if (!hasFunctionDef)
        {
            errors.Add("No function/class definition found");
            return 0.3;
        }

        if (code.Contains("import "))
        {
            var imports = System.Text.RegularExpressions.Regex.Matches(code, @"import\s+(\w+)");
            foreach (System.Text.RegularExpressions.Match m in imports)
            {
                if (m.Groups[1].Value is "os" or "sys" or "subprocess" or "shutil")
                {
                    errors.Add($"Potentially unsafe import: {m.Groups[1].Value}");
                    return 0;
                }
            }
        }

        var dangerPatterns = new[] { "exec(", "eval(", "__import__", "compile(" };
        foreach (var pattern in dangerPatterns)
        {
            if (code.Contains(pattern))
            {
                errors.Add($"Dangerous pattern detected: {pattern}");
                return 0;
            }
        }

        var bracketCount = 0;
        foreach (var c in code)
        {
            if (c is '(' or '[' or '{') bracketCount++;
            if (c is ')' or ']' or '}') bracketCount--;
        }
        if (bracketCount != 0)
        {
            errors.Add($"Unbalanced brackets: {bracketCount}");
            return 0.5;
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return errors.Count == 0 ? 1.0 : 0.5;
    }

    private async Task<double> CheckRuntimeBehaviorAsync(EvolutionCandidate candidate, CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(false);

        if (candidate.Code.Contains("return ") && !candidate.Code.Contains("return None"))
            return 1.0;

        if (candidate.Code.Contains("yield ") || candidate.Code.Contains("print("))
            return 0.8;

        return 0.9;
    }

    private double EvaluateSecurity(EvolutionCandidate candidate, List<string> warnings)
    {
        if (_securityConstraints.Count == 0)
            return 1.0;

        double totalScore = 0;
        double totalWeight = 0;

        foreach (var constraint in _securityConstraints)
        {
            var score = constraint.ComputeScore(candidate);
            totalScore += score * constraint.Weight;
            totalWeight += constraint.Weight;

            if (score < 1.0)
            {
                warnings.Add($"Security constraint '{constraint.Name}' failed");
            }
        }

        return totalWeight > 0 ? totalScore / totalWeight : 1.0;
    }

    private static double ComputeFitnessScore(
        double correctness, double security, double latencyMs, double computeUtil)
    {
        if (correctness < 1.0 || security < 1.0) return 0;

        var latencyScore = latencyMs > 0 ? 1000.0 / latencyMs : 1.0;
        var computeBonus = 1.0 + computeUtil * 0.2;
        return latencyScore * computeBonus * correctness * security;
    }

    private static EvaluationResult CreateRejectedResult(
        EvolutionCandidate candidate,
        double correctness,
        double security,
        List<string> warnings)
    {
        return new EvaluationResult(
            CandidateId: candidate.Id,
            CorrectnessScore: correctness,
            SecurityScore: security,
            LatencyMs: double.MaxValue,
            MemoryMb: 0,
            ComputeUtilization: 0,
            FitnessScore: 0,
            ProfilingMetrics: new(),
            Warnings: warnings,
            Passed: false);
    }
}
