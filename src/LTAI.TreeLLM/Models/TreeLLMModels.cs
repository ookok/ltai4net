using System.Text.Json.Serialization;

namespace LTAI.TreeLLM.Models;

public record RoutingCandidate
{
    public string Provider { get; init; } = "";
    public string Model { get; init; } = "";
    public string TaskType { get; init; } = "general";
    public Dictionary<string, double> Metrics { get; init; } = new();
    public Dictionary<string, string> Metadata { get; init; } = new();
}

public record RoutingDecision
{
    public string Provider { get; init; } = "";
    public string Model { get; init; } = "";
    public string Strategy { get; init; } = "";
    public double Score { get; init; }
    public Dictionary<string, double> Scores { get; init; } = new();
    public Dictionary<string, object?> Metadata { get; init; } = new();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public record LearnedProfile
{
    public string Provider { get; init; } = "";
    public int ContextLength { get; init; } = 32768;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReasoningCapable { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ToolCallCapable { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool StructuredOutput { get; init; }
    public double CostScore { get; init; } = 0.5;
    public double CapabilityScore { get; init; } = 0.5;
}

public record RoutingWeight
{
    public string TaskType { get; init; } = "";
    public string Provider { get; init; } = "";
    public double Weight { get; set; } = 1.0;
    public double SuccessRate { get; set; }
    public double AvgLatencyMs { get; set; }
    public int SampleCount { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

public class BetaBelief
{
    public double Alpha { get; set; } = 1.0;
    public double Beta { get; set; } = 1.0;
    public double PriorAlpha { get; }
    public double PriorBeta { get; }

    public BetaBelief(double priorAlpha = 2.0, double priorBeta = 1.0)
    {
        Alpha = priorAlpha;
        Beta = priorBeta;
        PriorAlpha = priorAlpha;
        PriorBeta = priorBeta;
    }

    public double Sample(Random rng)
    {
        return SampleBeta(rng, Alpha, Beta);
    }

    public void Observe(bool success)
    {
        if (success) Alpha += 1.0;
        else Beta += 1.0;
    }

    public void Decay(double rate = 0.05)
    {
        Alpha = PriorAlpha + (Alpha - PriorAlpha) * (1.0 - rate);
        Beta = PriorBeta + (Beta - PriorBeta) * (1.0 - rate);
    }

    public double Mean => Alpha / (Alpha + Beta);

    public static double SampleBeta(Random rng, double alpha, double beta)
    {
        var x = SampleGamma(rng, alpha);
        var y = SampleGamma(rng, beta);
        return x / (x + y);
    }

    private static double SampleGamma(Random rng, double shape)
    {
        if (shape < 1.0)
        {
            var u = rng.NextDouble();
            return SampleGamma(rng, shape + 1.0) * Math.Pow(u, 1.0 / shape);
        }

        var d = shape - 1.0 / 3.0;
        var c = 1.0 / Math.Sqrt(9.0 * d);

        while (true)
        {
            double x, v;
            do
            {
                x = SampleNormal(rng);
                v = 1.0 + c * x;
            } while (v <= 0);

            v = v * v * v;
            var u = rng.NextDouble();

            if (u < 1.0 - 0.0331 * (x * x) * (x * x))
                return d * v;

            if (Math.Log(u) < 0.5 * x * x + d * (1.0 - v + Math.Log(v)))
                return d * v;
        }
    }

    private static double SampleNormal(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BreakerState
{
    Closed,
    Open,
    HalfOpen
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GateState
{
    Accept,
    PredictOnly,
    Reject,
    Recalibrate
}

public record BreakerStats
{
    public string Provider { get; init; } = "";
    public BreakerState State { get; init; } = BreakerState.Closed;
    public int FailureCount { get; set; }
    public int SuccessCount { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTime? LastFailureTime { get; set; }
    public DateTime? LastSuccessTime { get; set; }
    public DateTime? TrippedAt { get; set; }
    public int TripCount { get; set; }
    public int TotalBlocked { get; set; }
}

public record CoherenceDecision
{
    public GateState State { get; init; }
    public double Confidence { get; init; }
    public Dictionary<string, double> Scores { get; init; } = new();
    public string Reason { get; init; } = "";
    public int Depth { get; init; }
    public double DataCompleteness { get; init; }
    public bool RequiresRecalibration { get; init; }
    public List<string> RecalibrationHints { get; init; } = new();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    public bool ShouldSimulate =>
        State == GateState.Accept || State == GateState.PredictOnly;

    public bool IsSafe =>
        State == GateState.Accept || State == GateState.PredictOnly;
}

public record ElectionSnapshot
{
    public List<ProviderScore> Scores { get; init; } = new();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string CandidatesHash { get; init; } = "";
}

public record ProviderScore
{
    public string Provider { get; init; } = "";
    public bool Alive { get; set; } = true;
    public bool IsFree { get; set; }
    public Dictionary<string, double> Scores { get; init; } = new();
    public double Total { get; set; }
    public double LpoScore { get; set; }
    public double Latency { get; set; }
    public double SuccessRate { get; set; }
    public double CapabilityMatch { get; set; }
    public double CostYuanPer1K { get; set; }
    public double AvgLatencyMs { get; set; }
}

public record StrategicPrinciple
{
    public string Id { get; init; } = "";
    public string Principle { get; init; } = "";
    public string Category { get; init; } = "";
    public List<string> SourceTraces { get; init; } = new();
    public int SuccessEvidence { get; set; }
    public int FailureEvidence { get; set; }
    public double ApplicabilityScore { get; set; }
    public DateTime LastUsed { get; set; } = DateTime.UtcNow;
    public string EmbeddingHint { get; init; } = "";
}

public record DistillationResult
{
    public int TracesProcessed { get; init; }
    public int PrinciplesDistilled { get; init; }
    public int PrinciplesReinforced { get; init; }
    public double DurationMs { get; init; }
}

public record PromptVariant
{
    public string Id { get; init; } = "";
    public string Text { get; init; } = "";
    public double Alpha { get; set; } = 3.0;
    public double Beta { get; set; } = 3.0;
}
