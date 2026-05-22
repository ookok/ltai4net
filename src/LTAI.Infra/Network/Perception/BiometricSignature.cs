using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace LTAI.Infra.Network.Perception;

public enum IdentityConfidence
{
    High,
    Moderate,
    Low,
    Unknown,
    Conflict
}

public sealed record KeystrokeProfile
{
    [JsonPropertyName("avg_inter_key_ms")]
    public double AvgInterKeyMs { get; init; }

    [JsonPropertyName("std_inter_key_ms")]
    public double StdInterKeyMs { get; init; }

    [JsonPropertyName("burst_rate")]
    public double BurstRate { get; init; }

    [JsonPropertyName("typo_rate")]
    public double TypoRate { get; init; }

    [JsonPropertyName("backspace_ratio")]
    public double BackspaceRatio { get; init; }

    [JsonPropertyName("sample_count")]
    public int SampleCount { get; init; }

    public double Distance(KeystrokeProfile other)
    {
        if (other.SampleCount == 0 && SampleCount == 0)
            return 0.0;
        if (other.SampleCount == 0 || SampleCount == 0)
            return 1.0;

        var avgDiff = Math.Abs(AvgInterKeyMs - other.AvgInterKeyMs) / Math.Max(100.0, Math.Max(AvgInterKeyMs, other.AvgInterKeyMs));
        var stdDiff = Math.Abs(StdInterKeyMs - other.StdInterKeyMs) / Math.Max(50.0, Math.Max(StdInterKeyMs, other.StdInterKeyMs));
        var burstDiff = Math.Abs(BurstRate - other.BurstRate);
        var typoDiff = Math.Abs(TypoRate - other.TypoRate);
        var backDiff = Math.Abs(BackspaceRatio - other.BackspaceRatio);

        var raw = (avgDiff + stdDiff + burstDiff + typoDiff + backDiff) / 5.0;
        return Math.Clamp(raw, 0.0, 1.0);
    }
}

public sealed record CommandVocabulary
{
    [JsonPropertyName("top_commands")]
    public List<string> TopCommands { get; init; } = new();

    [JsonPropertyName("complexity")]
    public double Complexity { get; init; }

    [JsonPropertyName("pipe_usage")]
    public double PipeUsage { get; init; }

    [JsonPropertyName("shell_preference")]
    public string ShellPreference { get; init; } = string.Empty;

    public double JaccardSimilarity(CommandVocabulary other)
    {
        var a = TopCommands.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var b = other.TopCommands.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (a.Count == 0 && b.Count == 0)
            return 1.0;

        var intersection = a.Intersect(b).Count();
        var union = a.Union(b).Count();
        return (double)intersection / union;
    }
}

public sealed record TemporalRhythm
{
    [JsonPropertyName("active_hours")]
    public int[] ActiveHours { get; init; } = new int[24];

    [JsonPropertyName("session_duration_avg")]
    public double SessionDurationAvg { get; init; }

    [JsonPropertyName("morning_person_score")]
    public double MorningPersonScore { get; init; }

    public double Distance(TemporalRhythm other)
    {
        if (ActiveHours.Length != 24 || other.ActiveHours.Length != 24)
            return 1.0;

        var sum = 0.0;
        var selfTotal = ActiveHours.Sum();
        var otherTotal = other.ActiveHours.Sum();

        var selfNorm = ActiveHours.Select(v => selfTotal > 0 ? v / (double)selfTotal : 0.0).ToArray();
        var otherNorm = other.ActiveHours.Select(v => otherTotal > 0 ? v / (double)otherTotal : 0.0).ToArray();

        for (var i = 0; i < 24; i++)
            sum += Math.Abs(selfNorm[i] - otherNorm[i]);

        return Math.Clamp(sum / 2.0, 0.0, 1.0);
    }
}

public sealed record ErrorSignature
{
    [JsonPropertyName("common_errors")]
    public Dictionary<string, int> CommonErrors { get; init; } = new();

    [JsonPropertyName("correction_rate")]
    public double CorrectionRate { get; init; }

    [JsonPropertyName("copy_paste_rate")]
    public double CopyPasteRate { get; init; }
}

public sealed record LanguageFingerprint
{
    [JsonPropertyName("avg_sentence_length")]
    public double AvgSentenceLength { get; init; }

    [JsonPropertyName("vocabulary_richness")]
    public double VocabularyRichness { get; init; }

    [JsonPropertyName("punctuation_style")]
    public string PunctuationStyle { get; init; } = string.Empty;

    [JsonPropertyName("emoji_usage")]
    public double EmojiUsage { get; init; }
}

public sealed record BiometricProfile
{
    [JsonPropertyName("user_id")]
    public string UserId { get; init; } = string.Empty;

    [JsonPropertyName("keystroke")]
    public KeystrokeProfile Keystroke { get; init; } = new();

    [JsonPropertyName("command")]
    public CommandVocabulary Command { get; init; } = new();

    [JsonPropertyName("temporal")]
    public TemporalRhythm Temporal { get; init; } = new();

    [JsonPropertyName("error")]
    public ErrorSignature Error { get; init; } = new();

    [JsonPropertyName("language")]
    public LanguageFingerprint Language { get; init; } = new();

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("last_updated")]
    public DateTime LastUpdated { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("active")]
    public bool Active { get; init; }

    public double MatchScore(BiometricProfile other)
    {
        var keystrokeScore = 1.0 - Keystroke.Distance(other.Keystroke);
        var commandScore = Command.JaccardSimilarity(other.Command);
        var temporalScore = 1.0 - Temporal.Distance(other.Temporal);
        var languageDist = 1.0 -
            (Math.Abs(Language.AvgSentenceLength - other.Language.AvgSentenceLength) /
             Math.Max(1.0, Math.Max(Language.AvgSentenceLength, other.Language.AvgSentenceLength)) +
             Math.Abs(Language.VocabularyRichness - other.Language.VocabularyRichness) +
             Math.Abs(Language.EmojiUsage - other.Language.EmojiUsage)) / 3.0;
        languageDist = Math.Clamp(languageDist, 0.0, 1.0);
        var errorDist = 1.0 - (Math.Abs(Error.CorrectionRate - other.Error.CorrectionRate) +
                               Math.Abs(Error.CopyPasteRate - other.Error.CopyPasteRate)) / 2.0;
        errorDist = Math.Clamp(errorDist, 0.0, 1.0);

        return keystrokeScore * 0.25 + commandScore * 0.30 + temporalScore * 0.20 + languageDist * 0.15 + errorDist * 0.10;
    }
}

public sealed class BiometricRegistry
{
    private static readonly Lazy<BiometricRegistry> _instance = new(() => new BiometricRegistry());

    public static BiometricRegistry Instance => _instance.Value;

    private readonly ILogger<BiometricRegistry>? _logger;
    private readonly ConcurrentDictionary<string, BiometricProfile> _profiles = new();
    private readonly object _activeLock = new();
    private string _activeProfileId = string.Empty;

    private readonly ConcurrentQueue<double> _keystrokeBuf = new();
    private const int MaxKeystrokeBuf = 100;

    private readonly ConcurrentQueue<string> _commandBuf = new();
    private const int MaxCommandBuf = 50;

    private readonly ConcurrentDictionary<string, int> _errorBuf = new();
    private double _correctionCount;
    private double _copyPasteCount;
    private double _totalActionCount;

    public BiometricRegistry()
    {
    }

    public BiometricRegistry(ILogger<BiometricRegistry> logger)
    {
        _logger = logger;
    }

    public BiometricProfile CreateProfile(
        string userId,
        KeystrokeProfile keystroke,
        CommandVocabulary command,
        TemporalRhythm temporal,
        ErrorSignature error,
        LanguageFingerprint language)
    {
        var profile = new BiometricProfile
        {
            UserId = userId,
            Keystroke = keystroke,
            Command = command,
            Temporal = temporal,
            Error = error,
            Language = language,
            CreatedAt = DateTime.UtcNow,
            LastUpdated = DateTime.UtcNow,
            Active = false
        };

        _profiles[userId] = profile;
        _logger?.LogInformation("Biometric profile created for user: {UserId}", userId);
        return profile;
    }

    public void SetActive(string userId)
    {
        lock (_activeLock)
        {
            _activeProfileId = userId;
            if (_profiles.TryGetValue(userId, out var existing))
            {
                _profiles[userId] = existing with { Active = true, LastUpdated = DateTime.UtcNow };
            }
            _logger?.LogInformation("Active profile set to: {UserId}", userId);
        }
    }

    public BiometricProfile? GetActive()
    {
        lock (_activeLock)
        {
            if (string.IsNullOrEmpty(_activeProfileId))
                return null;
            _profiles.TryGetValue(_activeProfileId, out var profile);
            return profile;
        }
    }

    public BiometricProfile? GetProfile(string userId)
    {
        _profiles.TryGetValue(userId, out var profile);
        return profile;
    }

    public void FeedKeystroke(double interKeyMs)
    {
        _keystrokeBuf.Enqueue(interKeyMs);
        while (_keystrokeBuf.Count > MaxKeystrokeBuf)
            _keystrokeBuf.TryDequeue(out _);

        var active = GetActive();
        if (active == null || active.Keystroke.SampleCount == 0)
            return;

        var currentMean = active.Keystroke.AvgInterKeyMs;
        var currentM2 = active.Keystroke.StdInterKeyMs * active.Keystroke.StdInterKeyMs * (active.Keystroke.SampleCount - 1);
        var currentN = active.Keystroke.SampleCount;

        var (newMean, newM2, newN) = _runningStd(interKeyMs, currentMean, currentM2, currentN);
        var newStd = newN > 1 ? Math.Sqrt(newM2 / (newN - 1)) : 0.0;

        var updatedKeystroke = active.Keystroke with
        {
            AvgInterKeyMs = newMean,
            StdInterKeyMs = newStd,
            SampleCount = newN
        };

        _profiles[active.UserId] = active with { Keystroke = updatedKeystroke, LastUpdated = DateTime.UtcNow };
    }

    public void FeedCommand(string command)
    {
        _commandBuf.Enqueue(command);
        while (_commandBuf.Count > MaxCommandBuf)
            _commandBuf.TryDequeue(out _);

        _totalActionCount++;
    }

    public void FeedError(Dictionary<string, int>? errorCounts = null, bool isCorrection = false, bool isCopyPaste = false)
    {
        _totalActionCount++;
        if (isCorrection) _correctionCount++;
        if (isCopyPaste) _copyPasteCount++;

        if (errorCounts != null)
        {
            foreach (var (key, count) in errorCounts)
                _errorBuf.AddOrUpdate(key, count, (_, existing) => existing + count);
        }
    }

    public IdentityConfidence VerifyIdentity(string? userId = null, double threshold = 0.70)
    {
        var targetProfile = !string.IsNullOrEmpty(userId) ? GetProfile(userId!) : GetActive();
        if (targetProfile == null)
            return IdentityConfidence.Unknown;

        var tempProfile = _buildTempProfile();
        if (tempProfile == null)
            return IdentityConfidence.Unknown;

        var score = targetProfile.MatchScore(tempProfile);

        if (score >= 0.90) return IdentityConfidence.High;
        if (score >= 0.70) return IdentityConfidence.Moderate;
        if (score >= 0.50) return IdentityConfidence.Low;
        return IdentityConfidence.Unknown;
    }

    public (string? UserId, IdentityConfidence Confidence, double Score) Identify()
    {
        var tempProfile = _buildTempProfile();
        if (tempProfile == null)
            return (null, IdentityConfidence.Unknown, 0.0);

        string? bestId = null;
        var bestScore = 0.0;
        BiometricProfile? firstProfile = null;
        var secondBestScore = 0.0;

        foreach (var (id, profile) in _profiles)
        {
            var score = profile.MatchScore(tempProfile);
            if (score > bestScore)
            {
                secondBestScore = bestScore;
                bestScore = score;
                bestId = id;
                firstProfile = profile;
            }
            else if (score > secondBestScore)
            {
                secondBestScore = score;
            }
        }

        if (bestId == null || firstProfile == null)
            return (null, IdentityConfidence.Unknown, 0.0);

        IdentityConfidence confidence;
        if (bestScore >= 0.90)
            confidence = IdentityConfidence.High;
        else if (bestScore >= 0.70)
            confidence = IdentityConfidence.Moderate;
        else if (bestScore >= 0.50)
            confidence = IdentityConfidence.Low;
        else
            confidence = IdentityConfidence.Unknown;

        if (bestScore >= 0.90 && secondBestScore >= 0.85 && bestScore - secondBestScore < 0.05)
            confidence = IdentityConfidence.Conflict;

        return (bestId, confidence, bestScore);
    }

    public string GetReport()
    {
        var active = GetActive();
        if (active == null)
            return "No active profile.";

        return $"Active: {active.UserId} | Samples: {active.Keystroke.SampleCount} | "
               + $"Avg IK: {active.Keystroke.AvgInterKeyMs:F1}ms | "
               + $"Commands: {active.Command.TopCommands.Count} | "
               + $"Complexity: {active.Command.Complexity:F2}";
    }

    public (int ProfileCount, string? ActiveUser) Stats()
    {
        return (_profiles.Count, GetActive()?.UserId);
    }

    private BiometricProfile? _buildTempProfile()
    {
        var keystrokeSamples = _keystrokeBuf.ToArray();
        if (keystrokeSamples.Length == 0 && _commandBuf.IsEmpty)
            return null;

        double avgIk = 0, stdIk = 0;
        var n = keystrokeSamples.Length;
        if (n > 0)
        {
            avgIk = keystrokeSamples.Average();
            stdIk = n > 1 ? Math.Sqrt(keystrokeSamples.Sum(x => (x - avgIk) * (x - avgIk)) / (n - 1)) : 0.0;
        }

        var commands = _commandBuf.ToArray();
        var topCommands = commands
            .GroupBy(c => c)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => g.Key)
            .ToList();

        var pipeCount = (double)commands.Count(c => c.Contains('|'));
        var complexity = pipeCount / Math.Max(1, commands.Length);

        var cmdVocab = new CommandVocabulary
        {
            TopCommands = topCommands,
            Complexity = complexity,
            PipeUsage = pipeCount / Math.Max(1.0, commands.Length),
            ShellPreference = "powershell"
        };

        var keystrokeProfile = new KeystrokeProfile
        {
            AvgInterKeyMs = avgIk,
            StdInterKeyMs = stdIk,
            SampleCount = n
        };

        var tempProfile = new BiometricProfile
        {
            UserId = "temp",
            Keystroke = keystrokeProfile,
            Command = cmdVocab,
            Error = new ErrorSignature
            {
                CommonErrors = new Dictionary<string, int>(_errorBuf),
                CorrectionRate = _totalActionCount > 0 ? _correctionCount / _totalActionCount : 0.0,
                CopyPasteRate = _totalActionCount > 0 ? _copyPasteCount / _totalActionCount : 0.0
            }
        };

        return tempProfile;
    }

    private static (double mean, double m2, int n) _runningStd(double newValue, double mean, double m2, int n)
    {
        n++;
        var delta = newValue - mean;
        mean += delta / n;
        var delta2 = newValue - mean;
        m2 += delta * delta2;
        return (mean, m2, n);
    }
}
