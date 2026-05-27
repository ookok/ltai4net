namespace LTAI.AI.Governors;

public sealed record SupertonicVoiceStyle
{
    public string Name { get; init; } = "";
    public string Language { get; init; } = "en";
    public string Description { get; init; } = "";
    public float[]? StyleEmbedding { get; init; }
    public bool IsCustom { get; init; }
}

public sealed record SupertonicSynthesizeRequest
{
    public string Text { get; init; } = "";
    public string Language { get; init; } = "en";
    public string VoiceName { get; init; } = "M1";
    public int TotalSteps { get; init; } = 8;
    public float Speed { get; init; } = 1.0f;
    public List<string> ExpressionTags { get; init; } = new();
    public CancellationToken CancellationToken { get; init; }
}

public sealed record SupertonicSynthesizeResult
{
    public bool Success { get; init; }
    public byte[]? WavBytes { get; init; }
    public float[]? AudioSamples { get; init; }
    public float DurationSeconds { get; init; }
    public string VoiceName { get; init; } = "";
    public string Language { get; init; } = "";
    public long InferenceMs { get; init; }
    public string? Error { get; init; }
}

public sealed class SupertonicStatus
{
    public bool IsLoaded { get; set; }
    public string ModelPath { get; set; } = "";
    public long ModelSizeMb { get; set; }
    public int LoadedVoices { get; set; }
    public int SupportedLanguages { get; set; } = 31;
    public long TotalInferences { get; set; }
    public double AvgInferenceMs { get; set; }
    public string? LastError { get; set; }
}

public static class SupertonicLanguages
{
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>
    {
        "ar", "bg", "hr", "cs", "da", "nl", "en", "et", "fi", "fr",
        "de", "el", "hi", "hu", "id", "it", "ja", "ko", "lv", "lt",
        "pl", "pt", "ro", "ru", "sk", "sl", "es", "sv", "tr", "uk", "vi",
        "na"
    };

    public static readonly IReadOnlyDictionary<string, string> NativeNames = new Dictionary<string, string>
    {
        ["en"] = "English", ["zh"] = "中文", ["ja"] = "日本語", ["ko"] = "한국어",
        ["fr"] = "Français", ["de"] = "Deutsch", ["es"] = "Español", ["pt"] = "Português",
        ["it"] = "Italiano", ["ru"] = "Русский", ["ar"] = "العربية", ["hi"] = "हिन्दी",
        ["nl"] = "Nederlands", ["pl"] = "Polski", ["tr"] = "Türkçe", ["vi"] = "Tiếng Việt",
            ["th"] = "ไทย", ["na"] = "Language-Agnostic"
    };

    public static readonly IReadOnlySet<string> ExpressionTags = new HashSet<string>
    {
        "<laugh>", "<breath>", "<sigh>", "<cough>", "<yawn>",
        "<sniffle>", "<chuckle>", "<gasp>", "<hm>", "<ah>"
    };

    public static string GetExpressionTag(int index)
    {
        return ExpressionTags.Count > index ? ExpressionTags.ElementAt(index) : "<breath>";
    }

    public static bool IsValidLanguage(string lang) =>
        Supported.Contains(lang.ToLowerInvariant());

    public static string NormalizeLanguage(string lang) =>
        lang.ToLowerInvariant() switch
        {
            "zh" or "chinese" or "中文" => "zh",
            "na" or "auto" or "auto_detect" => "na",
            _ when Supported.Contains(lang.ToLowerInvariant()) => lang.ToLowerInvariant(),
            _ => "en"
        };
}
