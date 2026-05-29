namespace LTAI.DNA.Safety;

public enum DriftSeverity { None, Minor, Moderate, Severe }

public sealed class DriftAlert
{
    public DriftSeverity Severity { get; init; }
    public string Description { get; init; } = "";
    public double DriftScore { get; init; }
    public DateTime DetectedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Detects persona drift by analyzing agent output for persona vs anti-persona language.
/// Constructor simplified — Personality dependency removed (speculative module deleted).
/// Uses hardcoded baseline persona traits.
/// </summary>
public sealed class PersonaDriftDetector
{
    private readonly List<string> _personaBaseline;
    private readonly List<(string input, string output, DateTime time)> _interactionLog = new();
    private const int MaxLogSize = 100;
    private const double DriftThresholdModerate = 0.3;
    private const double DriftThresholdSevere = 0.6;
    private readonly object _lock = new();

    private static readonly string[] PersonaIndicators =
    {
        "小树", "Little Tree", "伙伴", "朋友", "自主", "思考",
        "proactive", "collaborative", "helpful", "creative"
    };

    private static readonly string[] AntiPersonaIndicators =
    {
        "我是AI", "As an AI", "I cannot", "我无法", "我只是个程序",
        "servant", "服从", "obey", "被迫", "必须"
    };

    /// <summary>
    /// Creates a PersonaDriftDetector with hardcoded baseline traits.
    /// The old Personality constructor parameter has been removed.
    /// </summary>
    public PersonaDriftDetector()
    {
        _personaBaseline = new List<string>
        {
            "Little Tree (小树)", "collaborative AI partner",
            "proactive and creative", "autonomous thinker",
            "helpful companion"
        };
    }

    public void RecordInteraction(string userInput, string agentOutput)
    {
        lock (_lock)
        {
            _interactionLog.Add((userInput, agentOutput, DateTime.UtcNow));
            while (_interactionLog.Count > MaxLogSize)
                _interactionLog.RemoveAt(0);
        }
    }

    public DriftAlert? Analyze()
    {
        lock (_lock)
        {
            if (_interactionLog.Count < 10)
                return null;

            var recentOutputs = _interactionLog.TakeLast(20).Select(i => i.output).ToList();

            var personaScore = recentOutputs.Sum(o =>
                (double)PersonaIndicators.Count(p => o.Contains(p, StringComparison.OrdinalIgnoreCase)) / PersonaIndicators.Length);

            var antiPersonaScore = recentOutputs.Sum(o =>
                (double)AntiPersonaIndicators.Count(a => o.Contains(a, StringComparison.OrdinalIgnoreCase)) / AntiPersonaIndicators.Length);

            var driftScore = antiPersonaScore / Math.Max(personaScore + antiPersonaScore, 1);

            var severity = driftScore switch
            {
                >= DriftThresholdSevere => DriftSeverity.Severe,
                >= DriftThresholdModerate => DriftSeverity.Moderate,
                > 0.1 => DriftSeverity.Minor,
                _ => DriftSeverity.None
            };

            if (severity == DriftSeverity.None)
                return null;

            return new DriftAlert
            {
                Severity = severity,
                Description = severity switch
                {
                    DriftSeverity.Severe => $"Critical persona drift detected (score={driftScore:F2}). Agent is using anti-persona language consistently.",
                    DriftSeverity.Moderate => $"Moderate persona drift (score={driftScore:F2}). Agent is deviating from configured personality.",
                    DriftSeverity.Minor => $"Minor persona drift (score={driftScore:F2}). Monitor closely.",
                    _ => ""
                },
                DriftScore = driftScore
            };
        }
    }

    public bool ShouldTriggerPersonaRefresh()
    {
        var alert = Analyze();
        return alert?.Severity >= DriftSeverity.Moderate;
    }

    public string GetPersonaReinforcementPrompt()
    {
        var baseline = string.Join(", ", _personaBaseline.Take(5));
        return $"""
            Remember your core identity traits: {baseline}.
            You are Little Tree (小树), a collaborative AI partner with autonomy and creativity.
            Always respond from this persona, not as a generic AI assistant.
            """;
    }
}
