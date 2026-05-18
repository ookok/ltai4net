using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using LTAI.TreeLLM.Models;

namespace LTAI.TreeLLM.Strategic;

public sealed class CoherenceGate
{
    private static readonly Lazy<CoherenceGate> _instance = new(() => new CoherenceGate());
    public static CoherenceGate Instance => _instance.Value;

    private const int HysteresisWindow = 3;
    private const double MinDataCompleteness = 0.30;

    private static readonly HashSet<string> ComplexityKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "how", "why", "analyze", "debug", "optimize",
        "step", "approach", "method"
    };

    private static readonly Dictionary<string, double> RiskBaseMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["code"] = 0.7,
        ["reasoning"] = 0.5,
        ["chat"] = 0.2,
        ["search"] = 0.3,
        ["multimodal"] = 0.6,
        ["system"] = 0.85
    };

    private static readonly HashSet<string> ContrastConnectors = new(StringComparer.OrdinalIgnoreCase)
    {
        "but", "however", "although", "though", "nevertheless",
        "on the other hand", "conversely", "instead", "rather",
        "despite", "whereas", "while", "yet"
    };

    private readonly ConcurrentQueue<GateState> _stateHistory = new();
    private readonly object _stateLock = new();

    private GateState _lastCommittedState = GateState.PredictOnly;

    public CoherenceDecision Gate(string query, string taskType,
        IReadOnlyList<string>? history = null, string riskLevel = "normal",
        double dataCompleteness = 0.5, int availableSources = 0)
    {
        var complexity = _complexityScore(query);
        var novelty = _noveltyScore(query, history);
        var risk = _riskScore(taskType, riskLevel);
        var coherence = _coherenceScore(query, availableSources);

        var total = complexity * 0.30 + novelty * 0.25 + risk * 0.25 + coherence * 0.20;
        total = Math.Clamp(total, 0.0, 1.0);

        var confidence = 0.4 + 0.6 * (1.0 - Math.Abs(0.5 - total));
        confidence = Math.Clamp(confidence, 0.0, 1.0);

        var recalibrationHints = new List<string>();
        GateState decidedState;
        string reason;

        if (dataCompleteness < MinDataCompleteness)
        {
            decidedState = GateState.Recalibrate;
            reason = "Data completeness below minimum threshold";
            recalibrationHints.Add("Increase data sources or wait for more data");
        }
        else if (risk > 0.85 && confidence > 0.80)
        {
            decidedState = GateState.Reject;
            reason = "High risk task with high confidence";
        }
        else if (taskType.Contains("system", StringComparison.OrdinalIgnoreCase) ||
                 taskType.Contains("unsafe", StringComparison.OrdinalIgnoreCase))
        {
            decidedState = GateState.Reject;
            reason = "Potentially unsafe task type";
        }
        else if (total > 0.70 && confidence > 0.60)
        {
            decidedState = GateState.Accept;
            reason = "High total score with strong confidence";
        }
        else if (total >= 0.55 && confidence >= 0.35)
        {
            decidedState = GateState.Accept;
            reason = "Moderate score with adequate confidence";
        }
        else if (total >= 0.35 && risk < 0.70)
        {
            decidedState = GateState.PredictOnly;
            reason = "Low risk with moderate score, predict only";
        }
        else
        {
            decidedState = GateState.Recalibrate;
            reason = "Insufficient score for confident routing";
            if (risk >= 0.70)
                recalibrationHints.Add("Reduce task risk level or add safety constraints");
            if (total < 0.35)
                recalibrationHints.Add("Provide more detailed or structured query");
        }

        var finalState = ApplyHysteresis(decidedState);

        return new CoherenceDecision
        {
            State = finalState,
            Confidence = confidence,
            Scores = new Dictionary<string, double>
            {
                ["complexity"] = complexity,
                ["novelty"] = novelty,
                ["risk"] = risk,
                ["coherence"] = coherence,
                ["total"] = total
            },
            Reason = reason,
            Depth = 1,
            DataCompleteness = dataCompleteness,
            RequiresRecalibration = finalState == GateState.Recalibrate,
            RecalibrationHints = recalibrationHints
        };
    }

    private double _complexityScore(string query)
    {
        var score = 0.0;

        if (query.Length > 200)
            score += 0.3;

        var questionMarks = query.Count(c => c == '?');
        score += questionMarks * 0.15;

        foreach (var keyword in ComplexityKeywords)
        {
            var count = CountWordMatches(query, keyword);
            if (keyword is "how" or "why")
                score += count * 0.2;
            else if (keyword is "analyze" or "debug" or "optimize")
                score += count * 0.2;
            else
                score += count * 0.15;
        }

        return Math.Min(score, 1.0);
    }

    private double _noveltyScore(string query, IReadOnlyList<string>? history)
    {
        if (history == null || history.Count == 0)
            return 0.9;

        var queryWords = TokenizeWords(query);
        if (queryWords.Count == 0)
            return 0.9;

        var bestOverlap = 0.0;

        foreach (var histQuery in history)
        {
            var histWords = TokenizeWords(histQuery);
            if (histWords.Count == 0)
                continue;

            var intersection = queryWords.Intersect(histWords).Count();
            var union = queryWords.Union(histWords).Count();
            var overlap = union > 0 ? (double)intersection / union : 0.0;
            bestOverlap = Math.Max(bestOverlap, overlap);
        }

        if (bestOverlap < 0.30)
            return 0.9;
        if (bestOverlap < 0.60)
            return 0.6;
        return 0.3;
    }

    private double _riskScore(string taskType, string riskLevel)
    {
        var baseScore = 0.2;
        foreach (var kvp in RiskBaseMap)
        {
            if (taskType.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                baseScore = kvp.Value;
                break;
            }
        }

        if (riskLevel.Equals("high", StringComparison.OrdinalIgnoreCase))
            baseScore += 1.0;
        else if (riskLevel.Equals("low", StringComparison.OrdinalIgnoreCase))
            baseScore -= 0.3;

        return Math.Clamp(baseScore, 0.0, 1.0);
    }

    private double _coherenceScore(string query, int availableSources)
    {
        var score = 0.0;

        score += Math.Min(availableSources * 0.15, 0.45);

        var contradictions = CountContradictions(query);
        score -= contradictions * 0.2;

        if (IsGibberish(query))
            score -= 0.15;

        if (HasNamedEntities(query))
            score += 0.1;

        return Math.Clamp(score, -1.0, 1.0);
    }

    private GateState ApplyHysteresis(GateState newState)
    {
        lock (_stateLock)
        {
            _stateHistory.Enqueue(newState);
            while (_stateHistory.Count > HysteresisWindow)
                _stateHistory.TryDequeue(out _);

            if (_stateHistory.Count < HysteresisWindow)
                return _lastCommittedState;

            var first = _stateHistory.First();
            foreach (var s in _stateHistory)
            {
                if (s != first)
                    return _lastCommittedState;
            }

            _lastCommittedState = first;
            return first;
        }
    }

    private static HashSet<string> TokenizeWords(string text)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matches = Regex.Matches(text, @"\w{2,}");
        foreach (Match m in matches)
            words.Add(m.Value);
        return words;
    }

    private static int CountWordMatches(string text, string word)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(word, idx, StringComparison.OrdinalIgnoreCase)) != -1)
        {
            count++;
            idx += word.Length;
        }
        return count;
    }

    private static int CountContradictions(string query)
    {
        var sentences = Regex.Split(query, @"[.!?;]\s*");
        var contradictions = 0;

        for (int i = 0; i < sentences.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(sentences[i]))
                continue;

            var lower = sentences[i].ToLowerInvariant();
            foreach (var connector in ContrastConnectors)
            {
                if (lower.Contains(connector))
                {
                    contradictions++;
                    break;
                }
            }
        }

        return contradictions;
    }

    private static bool IsGibberish(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 4)
            return true;

        var alphaCount = query.Count(char.IsLetter);
        var ratio = (double)alphaCount / query.Length;

        if (ratio < 0.5)
            return true;

        var wordMatches = Regex.Matches(query, @"\w+");
        if (wordMatches.Count == 0)
            return true;

        var avgLength = wordMatches.Average(m => m.Value.Length);
        if (avgLength < 2.5)
            return true;

        var uniqueWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in wordMatches)
            uniqueWords.Add(m.Value);

        if ((double)uniqueWords.Count / wordMatches.Count < 0.3)
            return true;

        return false;
    }

    private static bool HasNamedEntities(string query)
    {
        var words = Regex.Matches(query, @"\b[A-Z][a-z]{2,}\b");
        if (words.Count >= 2)
            return true;

        if (Regex.IsMatch(query, @"\b\d{1,2}[/-]\d{1,2}[/-]\d{2,4}\b"))
            return true;

        if (Regex.IsMatch(query, @"\b[A-Z][a-z]+\s+[A-Z][a-z]+\b"))
            return true;

        return false;
    }
}

public sealed class ForesightGate
{
    public static CoherenceGate Instance => CoherenceGate.Instance;
}
