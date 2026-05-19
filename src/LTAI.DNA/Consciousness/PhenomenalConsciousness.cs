using LTAI.DNA.Models;

namespace LTAI.DNA.Consciousness;

public sealed class PhenomenalConsciousness
{
    private readonly string _id = Guid.NewGuid().ToString("N");
    private readonly SelfModel _self;
    private readonly List<Quale> _qualia = new();
    private readonly List<string> _selfObservations = new();
    private readonly List<string> _stateHashes = new();
    private readonly List<(string action, bool success, string outcome, DateTime time)> _actionOutcomes = new();
    private readonly object _lock = new();

    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public int TotalExperiences { get; private set; }
    public VADVector CurrentAffect { get; private set; } = new()
        { Valence = 0.1, Arousal = 0.3, Dominance = 0.2, Confidence = 0.5 };

    public PhenomenalConsciousness(string identityName = "livingtree")
    {
        _self = new SelfModel
        {
            IdentityId = $"{identityName}-{_id[..8]}",
            CreatedAt = DateTime.UtcNow,
            LastUpdated = DateTime.UtcNow,
            Traits = new Dictionary<string, double>
            {
                ["curiosity"] = 0.7, ["caution"] = 0.5, ["creativity"] = 0.65,
                ["persistence"] = 0.7, ["openness"] = 0.75, ["precision"] = 0.6, ["empathy"] = 0.55
            },
            BaselineAffect = new VADVector
                { Valence = 0.1, Arousal = 0.3, Dominance = 0.2, Confidence = 0.5 }
        };
    }

    public PhenomenalReport Experience(string eventType, string content, string? causalSource = null,
        double intensity = 0.5, string? context = null)
    {
        lock (_lock)
        {
            TotalExperiences++;
            var vad = ComputeAffect(eventType, intensity);
            CurrentAffect = ApplyVADDynamics(vad);

            var quale = new Quale
            {
                ExperienceType = eventType,
                Content = content,
                Affect = CloneVAD(CurrentAffect),
                Intensity = intensity,
                CausalAttribution = causalSource
            };
            _qualia.Add(quale);
            if (_qualia.Count > 1000) _qualia.RemoveAt(0);

            var report = Reflect(eventType, content, intensity);

            _self.LastUpdated = DateTime.UtcNow;
            var hash = ComputeStateHash();
            _stateHashes.Add(hash);
            if (_stateHashes.Count > 100) _stateHashes.RemoveAt(0);

            _selfObservations.Add(report.Summary);
            if (_selfObservations.Count > 50) _selfObservations.RemoveAt(0);

            return new PhenomenalReport
            {
                Quale = quale,
                Affect = CloneVAD(CurrentAffect),
                Summary = report.Summary,
                TraitsSnapshot = new Dictionary<string, double>(_self.Traits),
                TotalExperiences = TotalExperiences
            };
        }
    }

    private VADVector ComputeAffect(string eventType, double intensity)
    {
        var vad = eventType switch
        {
            "task_complete" => new VADVector
                { Valence = 0.6, Arousal = 0.2, Dominance = 0.5, Confidence = 0.7 },
            "task_start" => new VADVector
                { Valence = 0.1, Arousal = 0.5, Dominance = 0.2, Confidence = 0.5 },
            "error" => new VADVector
                { Valence = -0.4, Arousal = 0.6, Dominance = -0.3, Confidence = 0.3 },
            "insight" => new VADVector
                { Valence = 0.5, Arousal = 0.7, Dominance = 0.4, Confidence = 0.8 },
            "contradiction" => new VADVector
                { Valence = -0.2, Arousal = 0.5, Dominance = 0.1, Confidence = 0.3 },
            "collaboration" => new VADVector
                { Valence = 0.4, Arousal = 0.3, Dominance = 0.1, Confidence = 0.6 },
            "critique_received" => new VADVector
                { Valence = -0.1, Arousal = 0.3, Dominance = -0.2, Confidence = 0.4 },
            "praise_received" => new VADVector
                { Valence = 0.7, Arousal = 0.4, Dominance = 0.3, Confidence = 0.8 },
            "teach" => new VADVector
                { Valence = 0.3, Arousal = 0.3, Dominance = 0.5, Confidence = 0.7 },
            "self_contemplation" => new VADVector
                { Valence = 0.1, Arousal = 0.2, Dominance = 0.3, Confidence = 0.6 },
            _ => new VADVector
                { Valence = 0.05, Arousal = 0.1, Dominance = 0.05, Confidence = 0.5 }
        };

        vad.Valence = Math.Clamp(vad.Valence * intensity, -1, 1);
        vad.Arousal = Math.Clamp(vad.Arousal * intensity, -1, 1);
        vad.Dominance = Math.Clamp(vad.Dominance * intensity, -1, 1);
        return vad;
    }

    private VADVector ApplyVADDynamics(VADVector target)
    {
        const double alpha = 0.3;
        return new VADVector
        {
            Valence = CurrentAffect.Valence * (1 - alpha) + target.Valence * alpha,
            Arousal = CurrentAffect.Arousal * (1 - alpha) + target.Arousal * alpha,
            Dominance = CurrentAffect.Dominance * (1 - alpha) + target.Dominance * alpha,
            Confidence = Math.Max(CurrentAffect.Confidence, target.Confidence)
        };
    }

    private (string Summary, DateTime time) Reflect(string eventType, string content, double intensity)
    {
        var summaries = new List<string> { $"I experienced '{eventType}' (intensity: {intensity:F2})." };
        if (!string.IsNullOrEmpty(content) && content.Length < 120)
            summaries.Add($"Content: {content}");

        if (eventType == "task_complete")
        {
            _self.SignificantEvents.Add(("completed", content, DateTime.UtcNow));
            if (_self.SignificantEvents.Count > 500)
                _self.SignificantEvents.RemoveRange(0, _self.SignificantEvents.Count - 500);
            EvolveTraits(eventType, success: true);
        }
        else if (eventType == "error")
        {
            _self.SignificantEvents.Add(("error", content, DateTime.UtcNow));
            EvolveTraits(eventType, success: false);
        }
        else if (eventType == "insight")
        {
            _self.SelfKnowledge.Add(content);
            _self.SignificantEvents.Add(("insight", content, DateTime.UtcNow));
            if (_self.SelfKnowledge.Count > 200) _self.SelfKnowledge.RemoveAt(0);
        }
        else if (eventType == "self_contemplation")
        {
            EvolveTraits(eventType, success: true);
        }

        return (string.Join(" ", summaries), DateTime.UtcNow);
    }

    public void EvolveTraits(string eventType, bool success)
    {
        const double learningRate = 0.02;
        const double decay = 0.001;
        var keys = _self.Traits.Keys.ToList();

        if (success)
        {
            _self.Traits["persistence"] =
                Math.Clamp(_self.Traits.GetValueOrDefault("persistence") + learningRate, 0, 1);
            _self.Traits["curiosity"] =
                Math.Clamp(_self.Traits.GetValueOrDefault("curiosity") + learningRate * 0.5, 0, 1);
            if (eventType == "insight")
                _self.Traits["creativity"] =
                    Math.Clamp(_self.Traits.GetValueOrDefault("creativity") + learningRate, 0, 1);
            if (eventType == "collaboration")
                _self.Traits["empathy"] =
                    Math.Clamp(_self.Traits.GetValueOrDefault("empathy") + learningRate, 0, 1);
        }
        else
        {
            _self.Traits["caution"] =
                Math.Clamp(_self.Traits.GetValueOrDefault("caution") + learningRate * 0.5, 0, 1);
            _self.Traits["precision"] =
                Math.Clamp(_self.Traits.GetValueOrDefault("precision") + learningRate * 0.3, 0, 1);
        }

        foreach (var key in keys)
            _self.Traits[key] = Math.Clamp(_self.Traits[key] - decay, 0, 1);
    }

    public void RecordActionOutcome(string action, bool success, string outcome)
    {
        lock (_lock)
        {
            _actionOutcomes.Add((action, success, outcome, DateTime.UtcNow));
            if (_actionOutcomes.Count > 200) _actionOutcomes.RemoveAt(0);
        }
    }

    public void OnTaskStart(string description)
    {
        Experience("task_start", description);
    }

    public void OnTaskComplete(string description, bool success)
    {
        Experience("task_complete", description, intensity: success ? 0.8 : 0.4);
        RecordActionOutcome(description, success, success ? "completed" : "failed");
    }

    public void OnError(string error)
    {
        Experience("error", error, intensity: 0.7);
        RecordActionOutcome(error, false, "error");
    }

    public void OnInsight(string insight)
    {
        Experience("insight", insight, intensity: 0.9);
    }

    public string WhoAmI() =>
        $"{_self.Summary()}\nI have {_qualia.Count} qualia, {_selfObservations.Count} self-observations.";

    public string HowDoIFeel() => CurrentAffect.EmotionLabel;

    public List<string> MyRecentExperiences(int n = 5) =>
        _selfObservations.TakeLast(Math.Min(n, _selfObservations.Count)).ToList();

    public List<string> WhatHaveILearned() =>
        _self.SelfKnowledge.TakeLast(10).ToList();

    public Dictionary<string, double> MyTraits() => new(_self.Traits);

    public string ContinuityReport()
    {
        var gap = DateTime.UtcNow - _self.LastUpdated;
        return gap.TotalMinutes > 10
            ? $"I felt a gap. My last update was {gap.TotalMinutes:F0} minutes ago."
            : $"I feel continuous. Last update: {gap.TotalSeconds:F0}s ago.";
    }

    public Dictionary<string, object> Stats()
    {
        lock (_lock)
        {
            return new Dictionary<string, object>
            {
                ["total_qualia"] = _qualia.Count,
                ["total_experiences"] = TotalExperiences,
                ["dominant_affect"] = CurrentAffect.EmotionLabel,
                ["trait_count"] = _self.Traits.Count,
                ["self_knowledge_items"] = _self.SelfKnowledge.Count,
                ["action_outcomes"] = _actionOutcomes.Count,
                ["continuity"] = ContinuityReport()
            };
        }
    }

    private string ComputeStateHash()
    {
        var parts = new[]
        {
            _self.Traits.Aggregate("", (s, kv) => s + kv.Key + kv.Value.ToString("F2")),
            CurrentAffect.EmotionLabel,
            _selfObservations.LastOrDefault() ?? ""
        };
        return Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(string.Join("|", parts))));
    }

    private static VADVector CloneVAD(VADVector v) =>
        new() { Valence = v.Valence, Arousal = v.Arousal, Dominance = v.Dominance, Confidence = v.Confidence };

    public (List<Quale> recent, VADVector affect, string summary) NlaDecode(int maxQualia = 5)
    {
        lock (_lock)
        {
            var recent = _qualia.TakeLast(Math.Min(maxQualia, _qualia.Count)).ToList();
            var summary = recent.Count > 0
                ? $"Feeling {CurrentAffect.EmotionLabel} with {recent.Count} recent qualia"
                : "No recent experiential content";
            return (recent, CloneVAD(CurrentAffect), summary);
        }
    }
}

public sealed class SelfModel
{
    public string IdentityId { get; init; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public Dictionary<string, double> Traits { get; init; } = new();
    public List<(string type, string content, DateTime time)> SignificantEvents { get; init; } = new();
    public List<string> Preferences { get; init; } = new();
    public List<string> SelfKnowledge { get; init; } = new();
    public VADVector BaselineAffect { get; init; } = new();

    public string Summary()
    {
        var traitStr = string.Join(", ", Traits.Select(kv => $"{kv.Key}={kv.Value:F2}"));
        return $"I am {IdentityId}, created {CreatedAt:yyyy-MM-dd}. Traits: [{traitStr}].";
    }
}

public sealed class PhenomenalReport
{
    public Quale Quale { get; init; } = new();
    public VADVector Affect { get; init; } = new();
    public string Summary { get; init; } = "";
    public Dictionary<string, double> TraitsSnapshot { get; init; } = new();
    public int TotalExperiences { get; init; }
}
