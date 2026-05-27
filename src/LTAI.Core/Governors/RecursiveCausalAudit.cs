using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Governors;

public sealed record CausalAuditResult
{
    public bool Passed { get; init; }
    public double CoherenceScore { get; init; }
    public double SelfContradictionScore { get; init; }
    public double FactualHealthScore { get; init; }
    public int CheckedSteps { get; init; }
    public List<string> Violations { get; init; } = new();
    public string? Reasoning { get; init; }
    public TimeSpan AuditTime { get; init; }
}

public sealed class RecursiveCausalAudit
{
    private readonly Func<string, CancellationToken, Task<string>>? _auditor;
    private Func<string, string, CancellationToken, Task<bool>>? _factChecker;
    private readonly ILogger<RecursiveCausalAudit> _logger;
    private int _auditsRun;
    private int _auditsPassed;
    private int _auditsFailed;

    public int AuditsRun => _auditsRun;
    public int AuditsPassed => _auditsPassed;
    public double PassRate => _auditsRun > 0 ? (double)_auditsPassed / _auditsRun : 0;

    public RecursiveCausalAudit(
        Func<string, CancellationToken, Task<string>>? auditor = null,
        Func<string, string, CancellationToken, Task<bool>>? factChecker = null,
        ILogger<RecursiveCausalAudit>? logger = null)
    {
        _auditor = auditor;
        _factChecker = factChecker;
        _logger = logger ?? NullLogger<RecursiveCausalAudit>.Instance;
    }

    public void WireFactCheckModel(Func<string, CancellationToken, Task<string>> modelInvoker)
    {
        _factChecker = async (claims, answer, ct) =>
        {
            try
            {
                var prompt = "You are a factual verification assistant. Given a user query and claims extracted from an answer, " +
                    "determine if the claims are factually correct based on your knowledge. " +
                    "Respond with exactly 'VERIFIED' if all claims appear correct, or 'DISPUTED: <reason>' if any claim is likely false.\n\n" +
                    $"USER QUERY: {claims.Split('\n').FirstOrDefault()}\n\n" +
                    $"{claims}\n\nANSWER: {answer[..Math.Min(answer.Length, 1000)]}";

                var response = await modelInvoker(prompt, ct).ConfigureAwait(false);
                var trimmed = response.Trim();
                if (trimmed.StartsWith("VERIFIED", StringComparison.OrdinalIgnoreCase))
                    return true;
                _logger.LogDebug("Fact check disputed: {Response}", trimmed[..Math.Min(trimmed.Length, 200)]);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Fact check model invocation failed");
                return false;
            }
        };

        _logger.LogInformation("RC Audit: wired fact-check model provider");
    }

    public void SetFactChecker(Func<string, string, CancellationToken, Task<bool>> factChecker)
    {
        _factChecker = factChecker;
        _logger.LogInformation("RC Audit: custom fact-checker set");
    }

    public async Task<CausalAuditResult> AuditAsync(
        string query,
        string reasoningTrace,
        string finalAnswer,
        CancellationToken ct = default)
    {
        var sw = global::System.Diagnostics.Stopwatch.StartNew();
        Interlocked.Increment(ref _auditsRun);

        var steps = ExtractReasoningSteps(reasoningTrace);
        if (steps.Count == 0)
        {
            var noStepsPassed = !HasObviousContradiction(reasoningTrace, finalAnswer);
            if (noStepsPassed) Interlocked.Increment(ref _auditsPassed);
            else Interlocked.Increment(ref _auditsFailed);

            return new CausalAuditResult
            {
                Passed = noStepsPassed,
                CoherenceScore = noStepsPassed ? 0.7 : 0.3,
                FactualHealthScore = 0.5,
                CheckedSteps = 0,
                AuditTime = sw.Elapsed
            };
        }

        var violations = new List<string>();
        double totalCoherence = 0;

        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];

            if (!HasCausalAnchor(step))
            {
                violations.Add($"Step {i + 1}: Missing causal anchor — '{step[..Math.Min(step.Length, 60)]}'");
                continue;
            }

            if (i > 0 && ContradictsPriorStep(steps[i - 1], step))
            {
                violations.Add($"Step {i + 1}: Contradicts step {i} — '{step[..Math.Min(step.Length, 60)]}'");
            }

            totalCoherence += StepCoherence(step);
        }

        double avgCoherence = steps.Count > 0 ? totalCoherence / steps.Count : 0;
        bool finalMatchesTrace = TraceLeadsToAnswer(steps, finalAnswer);

        if (!finalMatchesTrace)
            violations.Add("Final answer does not follow from reasoning trace");

        bool passed = violations.Count == 0;
        if (passed) Interlocked.Increment(ref _auditsPassed);
        else Interlocked.Increment(ref _auditsFailed);

        var contradictionScore = steps.Count > 1
            ? (double)violations.Count(v => v.Contains("Contradicts")) / (steps.Count - 1)
            : 0;

        double factualScore = 1.0;
        if (_factChecker != null && !string.IsNullOrWhiteSpace(finalAnswer))
        {
            try
            {
                var factualPrompt = ExtractFactualClaims(finalAnswer, query);
                var factResult = await _factChecker(factualPrompt, finalAnswer, ct).ConfigureAwait(false);
                factualScore = factResult ? 1.0 : 0.3;
                if (!factResult)
                    violations.Add("Factual verification FAILED — answer contains unverifiable or false claims");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Factual checker invocation failed");
                factualScore = 0.7;
            }
        }

        var result = new CausalAuditResult
        {
            Passed = passed,
            CoherenceScore = avgCoherence,
            SelfContradictionScore = contradictionScore,
            FactualHealthScore = factualScore,
            CheckedSteps = steps.Count,
            Violations = violations,
            Reasoning = passed ? "Trace→Answer verified" : $"{violations.Count} violation(s) found",
            AuditTime = sw.Elapsed
        };

        if (_auditor != null && !passed)
        {
            try
            {
                var reviewPrompt = "Audit the reasoning trace. Identify and fix any causal gaps.\n\n" +
                    $"Query: {query}\nTrace: {reasoningTrace[..Math.Min(reasoningTrace.Length, 1000)]}\n" +
                    $"Answer: {finalAnswer}\nViolations: {string.Join("; ", violations)}\n\n" +
                    "Output JSON: {\"can_fix\": true/false, \"fixed_answer\": \"...\", \"fix_explanation\": \"...\"}";

                var reviewResponse = await _auditor(reviewPrompt, ct).ConfigureAwait(false);
                _logger.LogDebug("RCA auditor review: {Response}", reviewResponse[..Math.Min(reviewResponse.Length, 200)]);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RCA auditor review failed");
            }
        }

        return result;
    }

    private static List<string> ExtractReasoningSteps(string trace)
    {
        var steps = new List<string>();
        var lines = trace.Split('\n');

        string? currentStep = null;
        foreach (var line in lines)
        {
            if (Regex.IsMatch(line, @"^(step\s*\d+|第\s*\d+\s*步|首先|然后|接着|最后|finally|therefore|因此|所以)", RegexOptions.IgnoreCase))
            {
                if (currentStep != null) steps.Add(currentStep.Trim());
                currentStep = line;
            }
            else if (currentStep != null)
            {
                currentStep += " " + line;
            }
            else if (line.Trim().Length > 10)
            {
                currentStep = line;
            }
        }
        if (currentStep != null) steps.Add(currentStep.Trim());

        if (steps.Count == 0 && !string.IsNullOrWhiteSpace(trace))
        {
            var sentences = Regex.Split(trace, @"(?<=[.!?。！？])\s+");
            steps.AddRange(sentences.Where(s => s.Trim().Length > 10).Take(20));
        }

        return steps;
    }

    private static readonly Regex MeasurableEffect = new(
        @"\d+%|\d+\.\d+|\bincrease\b|\bdecrease\b|\breduce\b|\braise\b|\blower\b|\bhigher\b|" +
        @"\b升\b|\b降\b|\b提高\b|\b降低\b|\b增加\b|\b减少\b|\b大于\b|\b小于\b|" +
        @"\bleads?\sto\b|\bresults?\sin\b|\bcauses?\b|\b导致\b|\b引起\b|" +
        @"\b改善\b|\b恶化\b|\b优化\b|\b退化\b|\b显著\b|\b明显\b|\b大幅度\b|" +
        @"\b提升了?\b|\b下降了?\b|\b增长了?\b|\b缩减了?\b|\b翻倍\b|\b减半\b|" +
        @"\d+\s*倍|\d+\s*个|\d+\s*次|\d+\s*条",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool HasCausalAnchor(string step)
    {
        var causalMarkers = new[]
        {
            "because", "since", "therefore", "thus", "hence", "so",
            "因为", "所以", "因此", "由于", "由此", "从而", "于是"
        };

        var hasMarker = causalMarkers.Any(m =>
            step.Contains(m, StringComparison.OrdinalIgnoreCase));

        if (!hasMarker) return false;

        return MeasurableEffect.IsMatch(step);
    }

    private static bool ContradictsPriorStep(string prior, string current)
    {
        var negationPairs = new (string positive, string negative)[]
        {
            ("is", "is not"), ("does", "does not"), ("can", "cannot"),
            ("yes", "no"), ("true", "false"),
            ("是", "不是"), ("可以", "不能"), ("能", "不能"),
            ("正确", "错误"), ("对", "不对")
        };

        foreach (var (pos, neg) in negationPairs)
        {
            if (prior.Contains(pos, StringComparison.OrdinalIgnoreCase) &&
                current.Contains(neg, StringComparison.OrdinalIgnoreCase))
                return true;
            if (prior.Contains(neg, StringComparison.OrdinalIgnoreCase) &&
                current.Contains(pos, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (Math.Abs(prior.Length - current.Length) < 5 &&
            prior == current)
            return true;

        return false;
    }

    private static double StepCoherence(string step)
    {
        double score = 0.5;

        if (HasCausalAnchor(step)) score += 0.3;
        if (step.Length > 30) score += 0.1;
        if (Regex.IsMatch(step, @"\d+")) score += 0.1;

        return Math.Min(1.0, score);
    }

    private static bool TraceLeadsToAnswer(List<string> steps, string answer)
    {
        if (steps.Count == 0) return true;
        var lastStep = steps.Last().ToLowerInvariant();
        var answerLower = answer.ToLowerInvariant();
        var words = lastStep.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Any(w => w.Length > 3 && answerLower.Contains(w));
    }

    private bool HasObviousContradiction(string trace, string answer)
    {
        return ContradictsPriorStep(trace, answer);
    }

    private static string ExtractFactualClaims(string answer, string query)
    {
        var sentences = Regex.Split(answer, @"(?<=[.!?。！？])\s+");
        var factualCandidates = sentences
            .Where(s => s.Trim().Length > 15)
            .Where(s => Regex.IsMatch(s, @"\d+") || Regex.IsMatch(s, @"[A-Z][a-z]+", RegexOptions.IgnoreCase))
            .Take(5)
            .ToList();

        if (factualCandidates.Count == 0 && sentences.Length > 0)
            factualCandidates.Add(sentences[0]);

        return $"[QUERY]: {query}\n[CLAIMS TO VERIFY]:\n" +
            string.Join("\n", factualCandidates.Select((c, i) => $"{i + 1}. {c.Trim()}"));
    }
}
