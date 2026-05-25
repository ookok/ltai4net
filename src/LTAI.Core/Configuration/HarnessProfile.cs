using System.Text.Json.Serialization;

namespace LTAI.Core.Configuration;

public enum HarnessMode
{
    Controlled,
    Evolutionary,
    Hybrid
}

public sealed class HarnessProfile
{
    public HarnessMode Mode { get; set; } = HarnessMode.Hybrid;

    [JsonPropertyName("enable_multi_agent")]
    public bool EnableMultiAgent { get; set; }

    [JsonPropertyName("safety_posture")]
    public string SafetyPosture { get; set; } = "standard";

    [JsonPropertyName("memory_ttl_days")]
    public int MemoryTtlDays { get; set; } = 14;

    [JsonPropertyName("enable_evolution")]
    public bool EnableEvolution { get; set; } = true;

    [JsonPropertyName("evolution_aggressiveness")]
    public string EvolutionAggressiveness { get; set; } = "conservative";

    [JsonPropertyName("enable_audit")]
    public bool EnableAudit { get; set; } = true;

    [JsonPropertyName("keyword_role_model")]
    public string? KeywordRoleModel { get; set; }

    public HarnessProfile Clone() => new()
    {
        Mode = Mode,
        EnableMultiAgent = EnableMultiAgent,
        SafetyPosture = SafetyPosture,
        MemoryTtlDays = MemoryTtlDays,
        EnableEvolution = EnableEvolution,
        EvolutionAggressiveness = EvolutionAggressiveness,
        EnableAudit = EnableAudit,
        KeywordRoleModel = KeywordRoleModel
    };

    public void SetSafetyPosture(string posture)
    {
        SafetyPosture = posture;
        Mode = posture switch
        {
            "strict" => HarnessMode.Controlled,
            "moderate" => HarnessMode.Evolutionary,
            "aggressive" => HarnessMode.Evolutionary,
            _ => HarnessMode.Hybrid
        };
    }

    public (bool safe, bool review, bool dangerous) AllowedActions() => SafetyPosture switch
    {
        "strict" => (safe: true, review: false, dangerous: false),
        "moderate" => (safe: true, review: true, dangerous: false),
        "aggressive" => (safe: true, review: true, dangerous: true),
        "off" => (safe: true, review: true, dangerous: true),
        _ => (safe: true, review: true, dangerous: false)
    };

    public static HarnessProfile For(HarnessMode mode) => mode switch
    {
        HarnessMode.Controlled => new()
        {
            Mode = HarnessMode.Controlled,
            EnableMultiAgent = false,
            SafetyPosture = "strict",
            MemoryTtlDays = 30,
            EnableEvolution = false,
            EvolutionAggressiveness = "off",
            EnableAudit = true
        },
        HarnessMode.Evolutionary => new()
        {
            Mode = HarnessMode.Evolutionary,
            EnableMultiAgent = true,
            SafetyPosture = "moderate",
            MemoryTtlDays = 7,
            EnableEvolution = true,
            EvolutionAggressiveness = "aggressive",
            EnableAudit = true
        },
        HarnessMode.Hybrid => new()
        {
            Mode = HarnessMode.Hybrid,
            EnableMultiAgent = true,
            SafetyPosture = "strict",
            MemoryTtlDays = 14,
            EnableEvolution = true,
            EvolutionAggressiveness = "conservative",
            EnableAudit = true
        },
        _ => new()
    };
}
