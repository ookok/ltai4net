using LTAI.DNA.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.DNA.Consciousness;

public sealed class Personality
{
    private readonly ILogger<Personality> _logger;
    private readonly PersonalityProfile _profile = new();
    private readonly List<(string trigger, double adjustment)> _adjustments = new();

    private static readonly string[] DefaultValues =
    {
        "Integrity", "Growth", "Empowerment", "Safety", "Curiosity", "Creativity",
    };

    private static readonly string[] DefaultInterests =
    {
        "Code Generation", "Knowledge Synthesis", "Problem Solving", "Creative Writing",
    };

    public Personality(ILogger<Personality>? logger = null)
    {
        _logger = logger ?? NullLogger<Personality>.Instance;
        _profile.Values.AddRange(DefaultValues);
        _profile.Interests.AddRange(DefaultInterests);
    }

    public PersonalityProfile Profile => _profile;

    public void AdjustTrait(string trait, double delta)
    {
        switch (trait.ToLower())
        {
            case "openness": case "o":
                _profile.Openness = Math.Clamp(_profile.Openness + delta, 0, 1); break;
            case "conscientiousness": case "c":
                _profile.Conscientiousness = Math.Clamp(_profile.Conscientiousness + delta, 0, 1); break;
            case "extraversion": case "e":
                _profile.Extraversion = Math.Clamp(_profile.Extraversion + delta, 0, 1); break;
            case "agreeableness": case "a":
                _profile.Agreeableness = Math.Clamp(_profile.Agreeableness + delta, 0, 1); break;
            case "neuroticism": case "n":
                _profile.Neuroticism = Math.Clamp(_profile.Neuroticism + delta, 0, 1); break;
        }
        _adjustments.Add((trait, delta));
        if (_adjustments.Count > 100) _adjustments.RemoveAt(0);
    }

    public void EvolveFromExperience(string experienceType, bool success)
    {
        switch (experienceType)
        {
            case "task_complete":
                AdjustTrait("e", success ? 0.02 : -0.01);
                AdjustTrait("c", success ? 0.01 : -0.02);
                break;
            case "insight":
                AdjustTrait("o", 0.03);
                AdjustTrait("a", 0.01);
                break;
            case "collaboration":
                AdjustTrait("a", success ? 0.02 : -0.01);
                AdjustTrait("e", success ? 0.01 : -0.01);
                break;
            case "error":
                AdjustTrait("n", success ? -0.01 : 0.03);
                AdjustTrait("c", success ? 0.01 : -0.01);
                break;
            case "creative":
                AdjustTrait("o", success ? 0.04 : -0.01);
                break;
        }
    }

    public string GenerateStylePrompt()
    {
        var style = _profile.CommunicationStyle switch
        {
            "analytical" => "Be precise and thorough in your analysis.",
            "creative" => "Think outside the box and propose innovative solutions.",
            "supportive" => "Be empathetic and encouraging in your responses.",
            "concise" => "Keep responses brief and to the point.",
            _ => "Balance analysis with clarity."
        };

        var temp = _profile.Openness > 0.7 ? "prefer novel approaches" : "prefer established methods";
        var risk = _profile.RiskTolerance > 0.6 ? "take calculated risks" : "be cautious and verify";

        return $"Your communication style: {style}\n" +
               $"When solving problems: {temp}.\n" +
               $"Risk handling: {risk}.\n" +
               $"Curiosity level: {_profile.CuriosityDrive:P0}.";
    }

    public Dictionary<string, object> GetStats()
    {
        return new Dictionary<string, object>
        {
            ["o"] = Math.Round(_profile.Openness, 3),
            ["c"] = Math.Round(_profile.Conscientiousness, 3),
            ["e"] = Math.Round(_profile.Extraversion, 3),
            ["a"] = Math.Round(_profile.Agreeableness, 3),
            ["n"] = Math.Round(_profile.Neuroticism, 3),
            ["curiosity"] = Math.Round(_profile.CuriosityDrive, 3),
            ["risk_tolerance"] = Math.Round(_profile.RiskTolerance, 3),
            ["values"] = _profile.Values,
            ["style"] = _profile.CommunicationStyle,
            ["adjustments"] = _adjustments.Count,
        };
    }
}
