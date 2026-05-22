namespace LTAI.Agent.LoopUS;

public sealed class ConfidenceGateConfig
{
    public double ExitThreshold { get; set; } = 0.85;
    public int MaxLoops { get; set; } = 5;
    public double ImprovementMin { get; set; } = 0.02;
    public bool EnableSelectiveGating { get; set; } = true;
    public double DriftTolerance { get; set; } = 0.3;
    public bool EnableEarlyExit { get; set; } = true;
    public double QualityBonus { get; set; } = 0.15;
}

public sealed class LoopState
{
    public int LoopCount { get; set; }
    public double Confidence { get; set; }
    public double PreviousConfidence { get; set; }
    public double DriftScore { get; set; }
    public bool ShouldExit => Confidence >= 0.85 || (Confidence - PreviousConfidence < 0.02) || LoopCount >= 5;
    public string Reason { get; set; } = "";
    public List<double> ConfidenceHistory { get; set; } = new();
    public string BestOutput { get; set; } = "";
    public double BestConfidence { get; set; }
    public long TotalLatencyMs { get; set; }
}

public sealed class ConfidenceGate
{
    private readonly ConfidenceGateConfig _config;
    private int _totalDecisions, _earlyExits, _savedCalls;

    public ConfidenceGate(ConfidenceGateConfig? config = null) => _config = config ?? new();

    public LoopState Initialize()
    {
        Interlocked.Increment(ref _totalDecisions);
        return new LoopState { LoopCount = 0, Confidence = 0, PreviousConfidence = 0 };
    }

    public bool ShouldContinueLoop(LoopState state, string currentOutput, double confidence)
    {
        state.LoopCount++;
        state.PreviousConfidence = state.Confidence;
        state.Confidence = confidence;
        state.ConfidenceHistory.Add(confidence);

        var improvement = confidence - state.PreviousConfidence;
        state.DriftScore = ComputeDriftScore(state.ConfidenceHistory, currentOutput);

        if (state.Confidence > state.BestConfidence)
        {
            state.BestConfidence = state.Confidence;
            state.BestOutput = currentOutput;
        }

        if (confidence >= _config.ExitThreshold)
        {
            state.Reason = $"confidence_reached ({confidence:F2} >= {_config.ExitThreshold})";
            Interlocked.Increment(ref _earlyExits);
            return false;
        }

        if (state.LoopCount >= _config.MaxLoops)
        {
            state.Reason = $"max_loops ({state.LoopCount})";
            return false;
        }

        if (state.LoopCount > 1 && improvement < _config.ImprovementMin)
        {
            state.Reason = $"diminishing_returns (improvement={improvement:F3} < {_config.ImprovementMin})";
            Interlocked.Increment(ref _savedCalls);
            return false;
        }

        if (_config.EnableSelectiveGating && state.DriftScore > _config.DriftTolerance)
        {
            state.Reason = $"representation_drift ({state.DriftScore:F2} > {_config.DriftTolerance})";
            return false;
        }

        return true;
    }

    public double EstimateConfidence(string output, string? originalQuery = null, int loopCount = 1)
    {
        var signals = new List<double>();

        signals.Add(ComputeLengthScore(output) * 0.15);
        signals.Add(ComputeSelfConsistency(output) * 0.25);
        signals.Add(ComputeStructuralScore(output) * 0.20);
        signals.Add(ComputeSpecificityScore(output) * 0.20);

        if (!string.IsNullOrEmpty(originalQuery))
            signals.Add(ComputeQueryAlignment(output, originalQuery) * 0.20);

        var baseScore = signals.Sum();
        var loopBonus = loopCount > 1 ? Math.Log2(loopCount) * _config.QualityBonus : 0;

        return Math.Min(1.0, baseScore + loopBonus);
    }

    private static double ComputeLengthScore(string output)
    {
        var len = output.Length;
        if (len < 20) return 0.2;
        if (len < 100) return 0.6;
        if (len < 500) return 0.9;
        if (len < 2000) return 1.0;
        return 0.8;
    }

    private static double ComputeSelfConsistency(string output)
    {
        var sentences = output.Split(new[] { '。', '.', '!', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (sentences.Length < 2) return 0.5;

        var contradictions = sentences.Count(s => s.Contains("不") && s.Contains("但是"));
        return Math.Max(0.1, 1.0 - (double)contradictions / sentences.Length * 2);
    }

    private static double ComputeStructuralScore(string output)
    {
        var score = 0.0;
        if (System.Text.RegularExpressions.Regex.IsMatch(output, @"^#{1,3}\s")) score += 0.3;
        if (output.Contains("\n- ") || output.Contains("\n* ")) score += 0.3;
        if (System.Text.RegularExpressions.Regex.IsMatch(output, @"\d+\.\s")) score += 0.2;
        if (output.Contains("```")) score += 0.2;
        return Math.Min(1.0, score);
    }

    private static double ComputeSpecificityScore(string output)
    {
        var numbers = System.Text.RegularExpressions.Regex.Matches(output, @"\b\d+\.?\d*\b");
        var codeRefs = System.Text.RegularExpressions.Regex.Matches(output, @"`[^`]+`");
        return Math.Min(1.0, (numbers.Count * 0.05 + codeRefs.Count * 0.1));
    }

    private static double ComputeQueryAlignment(string output, string query)
    {
        var qWords = new HashSet<string>(query.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2));
        var oWords = new HashSet<string>(output.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2));
        var overlap = qWords.Intersect(oWords).Count();
        return qWords.Count > 0 ? (double)overlap / qWords.Count : 0.5;
    }

    private double ComputeDriftScore(List<double> history, string output)
    {
        if (history.Count < 3) return 0;
        var recent = history.TakeLast(3).ToList();
        var variance = recent.Average(v => Math.Pow(v - recent.Average(), 2));
        var outputComplexity = Math.Min(1.0, output.Length / 2000.0);
        return Math.Min(1.0, Math.Sqrt(variance) * 2 + outputComplexity * 0.3);
    }

    public Dictionary<string, object> GetStats() => new()
    {
        ["total_decisions"] = _totalDecisions,
        ["early_exits"] = _earlyExits,
        ["early_exit_rate"] = _totalDecisions > 0 ? $"{_earlyExits * 100.0 / _totalDecisions:F1}%" : "0%",
        ["saved_calls"] = _savedCalls,
        ["avg_reduction"] = _totalDecisions > 0 ? $"{_savedCalls * 100.0 / _totalDecisions:F1}%" : "0%",
        ["config"] = new { _config.ExitThreshold, _config.MaxLoops, _config.ImprovementMin, _config.DriftTolerance }
    };
}

public static class LoopUSIntegration
{
    public static async Task<string> RunWithLoopUSAsync(
        Func<string, Task<string>> llmFn,
        string query,
        ConfidenceGateConfig? config = null)
    {
        var gate = new ConfidenceGate(config ?? new());
        var state = gate.Initialize();
        var currentInput = query;

        while (true)
        {
            var output = await llmFn(currentInput);
            var confidence = gate.EstimateConfidence(output, query, state.LoopCount);

            if (!gate.ShouldContinueLoop(state, output, confidence))
            {
                state.BestOutput = state.BestConfidence > confidence ? state.BestOutput : output;
                break;
            }

            currentInput = BuildRefinementPrompt(query, output, state.LoopCount);
        }

        return state.BestOutput;
    }

    private static string BuildRefinementPrompt(string query, string previousOutput, int loopCount)
    {
        return $"""
Refine your previous answer (iteration {loopCount + 1}).

Original query: {query}

Previous response: {previousOutput[..Math.Min(1000, previousOutput.Length)]}

Instructions:
1. Identify and fix any errors or omissions
2. Add missing details or evidence
3. Improve clarity and structure
4. If the answer is already complete, respond with "REFINE_COMPLETE" and the original answer
""";
    }
}
