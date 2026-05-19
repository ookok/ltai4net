using System.Diagnostics;
using System.Text.RegularExpressions;
using LTAI.TreeLLM.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.TreeLLM.Adversarial;

public sealed class AdversarialSelfPlay
{
    private const int MAX_ROUNDS = 3;
    private const double CONVERGENCE_THRESHOLD = 0.85;
    private const int MIN_ANSWER_LENGTH = 50;
    private const int ROUND_TIMEOUT_MS = 60000;

    private static readonly Lazy<AdversarialSelfPlay> _instance = new(() => new AdversarialSelfPlay());
    public static AdversarialSelfPlay Instance => _instance.Value;

    private ILogger<AdversarialSelfPlay>? _logger;

    private AdversarialSelfPlay() { }

    public void SetLogger(ILogger<AdversarialSelfPlay> logger) => _logger = logger;

    public async Task<SelfPlayResult> Play(
        string modelOutput,
        string originalQuery,
        Func<string, string, Task<string>> chatFn,
        string modelName)
    {
        var sw = Stopwatch.StartNew();
        var result = new SelfPlayResult
        {
            OriginalAnswer = modelOutput,
            FinalAnswer = modelOutput,
            Rounds = new List<RebuttalRound>()
        };

        if (modelOutput.Length < MIN_ANSWER_LENGTH)
        {
            result.Status = "trivial_skipped";
            return result;
        }

        var currentAnswer = modelOutput;
        var totalTokens = 0;

        for (var roundNum = 1; roundNum <= MAX_ROUNDS; roundNum++)
        {
            var roundSw = Stopwatch.StartNew();
            var counterArgs = await _GenerateCounterArgs(currentAnswer, originalQuery, roundNum, chatFn, modelName);

            if (counterArgs.Count == 0)
            {
                result.ConvergenceRound = roundNum;
                result.Status = "converged_no_counter_args";
                break;
            }

            var revisionPrompt = _BuildRevisionPrompt(originalQuery, currentAnswer, counterArgs, roundNum);
            totalTokens += revisionPrompt.Length / 4;

            string revised;
            try
            {
                using var cts = new CancellationTokenSource(ROUND_TIMEOUT_MS);
                revised = await chatFn(revisionPrompt, $"Revise considering counter-arguments round {roundNum}");
            }
            catch (OperationCanceledException)
            {
                _logger?.LogWarning("SelfPlay round {Round} timed out", roundNum);
                result.Status = "timeout";
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "SelfPlay round {Round} failed", roundNum);
                break;
            }

            totalTokens += revised.Length / 4;
            var changes = _DetectChanges(currentAnswer, revised);
            var jaccard = _JaccardWordSimilarity(currentAnswer, revised);

            var rebuttalRound = new RebuttalRound
            {
                RoundNum = roundNum,
                OriginalAnswer = currentAnswer,
                CounterArguments = counterArgs,
                RevisedAnswer = revised,
                ChangesMade = changes,
                JaccardToPrevious = jaccard,
                TokensSpent = revised.Length / 4,
                LatencyMs = roundSw.ElapsedMilliseconds
            };

            result.Rounds.Add(rebuttalRound);

            currentAnswer = revised;

            if (jaccard >= CONVERGENCE_THRESHOLD)
            {
                result.ConvergenceRound = roundNum;
                result.Status = "converged";
                break;
            }
        }

        result.FinalAnswer = currentAnswer;
        result.TotalTokens = totalTokens;
        result.TotalLatencyMs = sw.ElapsedMilliseconds;
        result.DepthGain = _EstimateDepthGain(result.Rounds);

        if (string.IsNullOrWhiteSpace(result.Status))
            result.Status = result.Rounds.Count >= MAX_ROUNDS ? "max_rounds" : "completed";

        return result;
    }

    private async Task<List<string>> _GenerateCounterArgs(
        string answer,
        string query,
        int roundNum,
        Func<string, string, Task<string>> chatFn,
        string modelName)
    {
        string attackPrompt = roundNum switch
        {
            1 => $@"You are an adversarial critic. Identify ALL flaws in the following answer.
Consider: logical errors, factual inaccuracies, missing edge cases, unclear reasoning, unsupported assumptions.

Query: {query}
Answer: {answer}

List each flaw as a numbered bullet point. Be specific and constructive. If no flaws exist, respond with: NO_MEANINGFUL_FLAWS",

            2 => $@"You are a rigorous peer reviewer. The answer below has been revised once.
Now identify DEEPER structural issues: reasoning gaps, implicit assumptions, oversimplification, cherry-picked examples.

Query: {query}
Answer: {answer}

List each issue as a numbered bullet point. If no deeper issues exist, respond with: NO_MEANINGFUL_FLAWS",

            _ => $@"You are a final quality auditor. This answer has been revised multiple times.
Identify any REMAINING issues that were not addressed. Be extremely precise.

Query: {query}
Answer: {answer}

List any remaining issues as numbered bullet points. If the answer is fully satisfactory, respond with: NO_MEANINGFUL_FLAWS"
        };

        try
        {
            var response = await chatFn(attackPrompt, $"Critique the answer for flaws (round {roundNum})");
            return _ParseCounterArgs(response);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "SelfPlay counter-arg generation failed round {Round}", roundNum);
            return new List<string>();
        }
    }

    private static List<string> _ParseCounterArgs(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return new List<string>();

        if (response.Trim().Equals("NO_MEANINGFUL_FLAWS", StringComparison.OrdinalIgnoreCase))
            return new List<string>();

        var args = new List<string>();
        var lines = response.Split('\n');
        var current = "";

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            if (Regex.IsMatch(trimmed, @"^\d+[\.\)]\s"))
            {
                if (!string.IsNullOrWhiteSpace(current))
                    args.Add(current.Trim());

                current = Regex.Replace(trimmed, @"^\d+[\.\)]\s", "");
            }
            else if (trimmed.StartsWith("-") || trimmed.StartsWith("*"))
            {
                if (!string.IsNullOrWhiteSpace(current))
                    args.Add(current.Trim());

                current = trimmed.Substring(1).Trim();
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(current))
                    current += " " + trimmed;
            }
        }

        if (!string.IsNullOrWhiteSpace(current))
            args.Add(current.Trim());

        args = args.Where(a => a.Length > 5).ToList();
        return args.Take(6).ToList();
    }

    private static string _BuildRevisionPrompt(
        string query,
        string currentAnswer,
        List<string> counterArgs,
        int roundNum)
    {
        var argsList = string.Join("\n", counterArgs.Select((a, i) => $"{i + 1}. {a}"));

        return $@"Your answer has been reviewed by an adversarial critic. The following issues were identified:

{argsList}

Original Query: {query}
Your Current Answer: {currentAnswer}

Please revise your answer to address EACH of the issues above. Produce a complete, improved answer.
Do not acknowledge the revision process in your response - just provide the corrected answer.";
    }

    private static List<string> _DetectChanges(string original, string revised)
    {
        var originalLines = original.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var revisedLines = revised.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return revisedLines.Except(originalLines).Take(20).ToList();
    }

    private static double _JaccardWordSimilarity(string a, string b)
    {
        var wordsA = TokenizeWords(a);
        var wordsB = TokenizeWords(b);

        if (wordsA.Count == 0 && wordsB.Count == 0)
            return 1.0;

        var intersection = wordsA.Intersect(wordsB).Count();
        var union = wordsA.Union(wordsB).Count();

        return union > 0 ? (double)intersection / union : 0.0;
    }

    private static double _EstimateDepthGain(List<RebuttalRound> rounds)
    {
        if (rounds.Count == 0)
            return 0.0;

        var changeRounds = rounds.Count(r => r.ChangesMade.Count > 0);
        var avgDivergence = rounds.Count > 0
            ? rounds.Average(r => 1.0 - r.JaccardToPrevious)
            : 0.0;

        var gain = changeRounds * 0.3 + avgDivergence * 0.5;
        return Math.Min(gain, 1.0);
    }

    public async Task<(string answer, bool passed, Dictionary<string, double> scores)> SelfVerifyBeforeOutput(
        string answer,
        string query,
        Func<string, string, Task<string>> chatFn,
        string modelName,
        double verifyThreshold = 0.7)
    {
        var scores = new Dictionary<string, double>();

        var chainScore = _CheckChainCompleteness(answer);
        var consistencyScore = _CheckSelfConsistency(answer);
        var assumptionScore = _CheckAssumptionAwareness(answer);
        var gapScore = _CheckLogicalGaps(answer);

        scores["chain_completeness"] = chainScore;
        scores["self_consistency"] = consistencyScore;
        scores["assumption_awareness"] = assumptionScore;
        scores["logical_gaps"] = gapScore;

        var totalScore = chainScore * 0.35 + consistencyScore * 0.35 + assumptionScore * 0.30;
        scores["total"] = totalScore;

        if (totalScore >= verifyThreshold)
            return (answer, true, scores);

        _logger?.LogWarning("SelfVerify failed (score={Score:F2}), regenerating", totalScore);

        var fixPrompt = $@"Your previous answer had quality issues:
- Chain completeness: {chainScore:F2}
- Self-consistency: {consistencyScore:F2}
- Assumption awareness: {assumptionScore:F2}

Query: {query}
Previous Answer: {answer}

Please regenerate a corrected answer that addresses these quality concerns.";

        try
        {
            var fixedAnswer = await chatFn(fixPrompt, "Regenerate with improved quality");
            return (fixedAnswer, false, scores);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "SelfVerify regeneration failed");
            return (answer, false, scores);
        }
    }

    public async Task<string> ReflectAndRefine(
        string answer,
        string query,
        Func<string, string, Task<string>> chatFn,
        string modelName,
        int maxReflections = 3)
    {
        var current = answer;

        for (var i = 0; i < maxReflections; i++)
        {
            var reflectPrompt = $@"<reflect>
Review your own answer critically. Identify weaknesses, gaps, or improvements.
Think step by step about what could be better.
</reflect>

Query: {query}
Your Answer: {current}

After your reflection in <reflect>...</reflect> tags, provide your refined answer after the closing </reflect> tag.";

            try
            {
                var response = await chatFn(reflectPrompt, "Reflect on your answer and refine it");

                var refined = _ParseReflectionResponse(response, current);
                var jaccard = _JaccardWordSimilarity(current, refined);

                if (jaccard > 0.92)
                {
                    _logger?.LogDebug("ReflectAndRefine converged at iteration {Iteration}", i + 1);
                    return refined;
                }

                current = refined;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "ReflectAndRefine iteration {Iteration} failed", i + 1);
                break;
            }
        }

        return current;
    }

    private static string _ParseReflectionResponse(string response, string fallback)
    {
        if (string.IsNullOrWhiteSpace(response))
            return fallback;

        var lastReflectEnd = response.LastIndexOf("</reflect>", StringComparison.OrdinalIgnoreCase);
        if (lastReflectEnd < 0)
            return response;

        var after = response.Substring(lastReflectEnd + "</reflect>".Length).Trim();
        return string.IsNullOrWhiteSpace(after) ? fallback : after;
    }

    private static double _CheckChainCompleteness(string answer)
    {
        var stepMarkers = new[]
        {
            @"step\s*\d+", @"\bfirst\b", @"\bsecond\b", @"\bthird\b",
            @"\bnext\b", @"\bthen\b", @"\bfinally\b", @"\blastly\b",
            @"\d+\.\s", @"\d+\)\s", @"\bnumbered\b"
        };

        var score = 0.0;
        foreach (var marker in stepMarkers)
        {
            if (Regex.IsMatch(answer, marker, RegexOptions.IgnoreCase))
                score += 0.15;
        }

        var sentences = Regex.Split(answer, @"[.!?]\s+");
        var meaningfulSentences = sentences.Count(s => s.Trim().Length > 10);
        if (meaningfulSentences >= 5)
            score += 0.2;

        return Math.Min(score, 1.0);
    }

    private static double _CheckSelfConsistency(string answer)
    {
        var score = 1.0;
        var sentences = Regex.Split(answer, @"[.!?]\s+")
            .Where(s => s.Trim().Length > 10)
            .ToList();

        var negationCount = sentences.Count(s =>
            Regex.IsMatch(s, @"\b(not|no|never|cannot|don't|doesn't|isn't|won't)\b", RegexOptions.IgnoreCase));

        var negationPairs = FindNegationMismatches(sentences);
        score -= negationPairs * 0.25;

        return Math.Max(score, 0.0);
    }

    private static int FindNegationMismatches(List<string> sentences)
    {
        var mismatches = 0;

        for (var i = 0; i < sentences.Count; i++)
        {
            for (var j = i + 1; j < sentences.Count; j++)
            {
                var si = sentences[i].ToLowerInvariant();
                var sj = sentences[j].ToLowerInvariant();

                var hasNegI = Regex.IsMatch(si, @"\b(not|no|never|cannot|don't|doesn't|isn't|won't)\b");
                var hasNegJ = Regex.IsMatch(sj, @"\b(not|no|never|cannot|don't|doesn't|isn't|won't)\b");

                var siSet = TokenizeWordsSet(si);
                var sjSet = TokenizeWordsSet(sj);
                var commonWords = siSet.Intersect(sjSet).Count();
                var minWords = Math.Min(siSet.Count, sjSet.Count);
                var similarity = minWords > 0 ? (double)commonWords / minWords : 0.0;

                if (similarity > 0.3 && hasNegI != hasNegJ)
                    mismatches++;
            }
        }

        return mismatches;
    }

    private static double _CheckAssumptionAwareness(string answer)
    {
        var awarenessMarkers = new[]
        {
            @"\b(assum|assuming|assume)\b",
            @"\bif\b.*\bthen\b",
            @"\bprovided\b.*\bthat\b",
            @"\bthis depends on\b",
            @"\bnotably\b",
            @"\bimportantly\b",
            @"\bnote that\b"
        };

        var score = 0.0;
        foreach (var marker in awarenessMarkers)
        {
            if (Regex.IsMatch(answer, marker, RegexOptions.IgnoreCase))
                score += 0.2;
        }

        var hedgeCount = Regex.Matches(answer, @"\b(may|might|could|possibly|potentially|likely|perhaps)\b",
            RegexOptions.IgnoreCase).Count;
        score += Math.Min(hedgeCount * 0.1, 0.3);

        return Math.Min(score, 1.0);
    }

    private static double _CheckLogicalGaps(string answer)
    {
        var score = 0.5;

        var connectors = new[] { "because", "therefore", "thus", "hence", "consequently", "as a result" };
        var connectorCount = connectors.Sum(c =>
            Regex.Matches(answer, $@"\b{Regex.Escape(c)}\b", RegexOptions.IgnoreCase).Count);

        if (connectorCount >= 3)
            score += 0.3;
        else if (connectorCount >= 1)
            score += 0.1;

        if (Regex.IsMatch(answer, @"\b(however|but|although|on the other hand)\b", RegexOptions.IgnoreCase))
            score += 0.1;

        var sentences = Regex.Split(answer, @"[.!?]\s+").Where(s => s.Trim().Length > 10).ToList();
        if (sentences.Count > 3)
        {
            var avgLength = sentences.Average(s => s.Length);
            if (avgLength > 40)
                score += 0.1;
        }

        return Math.Min(score, 1.0);
    }

    private static HashSet<string> TokenizeWords(string text)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matches = Regex.Matches(text, @"\w{2,}");
        foreach (Match m in matches)
            words.Add(m.Value);
        return words;
    }

    private static HashSet<string> TokenizeWordsSet(string text)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matches = Regex.Matches(text, @"\w{2,}");
        foreach (Match m in matches)
            words.Add(m.Value);
        return words;
    }
}
