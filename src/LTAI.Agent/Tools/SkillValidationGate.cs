using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Tools;

public sealed record ValidationSample
{
    public string Query { get; init; } = "";
    public string[] ExpectedBehaviors { get; init; } = [];
    public string Category { get; init; } = "general";
}

public sealed record ValidationResult
{
    public bool Accepted { get; init; }
    public double OldScore { get; init; }
    public double NewScore { get; init; }
    public string Reason { get; init; } = "";
    public string[] Improvements { get; init; } = [];
    public string[] Regressions { get; init; } = [];
}

public sealed class SkillValidationGate
{
    private readonly IChatClient _judge;
    private readonly ILogger<SkillValidationGate> _logger;
    private readonly string _samplesPath;
    private List<ValidationSample> _samples = [];
    private readonly object _lock = new();

    private const double AcceptanceThreshold = 0.05;
    private const int MaxSamples = 20;

    public SkillValidationGate(
        IChatClient judge,
        ILogger<SkillValidationGate> logger,
        string skillsDir)
    {
        _judge = judge;
        _logger = logger;
        _samplesPath = Path.Combine(skillsDir, ".skillopt", "validation_samples.json");
        LoadSamples();
        if (_samples.Count == 0)
            SeedDefaultSamples();
    }

    public IReadOnlyList<ValidationSample> Samples
    {
        get { lock (_lock) return _samples.ToList(); }
    }

    public void AddSample(ValidationSample sample)
    {
        lock (_lock)
        {
            _samples.Add(sample);
            if (_samples.Count > MaxSamples)
                _samples = _samples[^MaxSamples..];
            SaveSamples();
        }
    }

    public async Task<ValidationResult> ValidateAsync(
        string skillName, string? oldContent, string newContent, CancellationToken ct = default)
    {
        List<ValidationSample> snapshot;
        lock (_lock) snapshot = _samples.ToList();

        if (snapshot.Count == 0)
            return new ValidationResult { Accepted = true, OldScore = 0.5, NewScore = 0.5, Reason = "No validation samples" };

        var oldScore = 0.0;
        if (!string.IsNullOrEmpty(oldContent))
            oldScore = await ScoreSkillAsync(skillName + " (old)", oldContent, snapshot, ct).ConfigureAwait(false);

        var newScore = await ScoreSkillAsync(skillName + " (new)", newContent, snapshot, ct).ConfigureAwait(false);

        var scoreDelta = newScore - oldScore;
        var accepted = scoreDelta >= AcceptanceThreshold;

        var result = new ValidationResult
        {
            Accepted = accepted,
            OldScore = Math.Round(oldScore, 4),
            NewScore = Math.Round(newScore, 4),
            Reason = accepted
                ? $"Score improved by {scoreDelta:+0.0000} (threshold: {AcceptanceThreshold:0.0000})"
                : $"Score delta {scoreDelta:+0.0000} below threshold {AcceptanceThreshold:0.0000}"
        };

        _logger.LogInformation(
            "[ValidationGate] {Skill}: old={Old:F4} new={New:F4} delta={Delta:+0.0000} accepted={Accepted}",
            skillName, oldScore, newScore, scoreDelta, accepted);

        return result;
    }

    private async Task<double> ScoreSkillAsync(string label, string skillContent, List<ValidationSample> samples, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are evaluating a skill document that guides an AI assistant.");
        sb.AppendLine("Rate how well this skill prepares the assistant on a scale of 0.0 to 1.0.");
        sb.AppendLine();
        sb.AppendLine("=== SKILL DOCUMENT ===");
        sb.AppendLine(skillContent.Length > 3000 ? skillContent[..3000] + "\n...(truncated)" : skillContent);
        sb.AppendLine();
        sb.AppendLine("=== EVALUATION QUERIES ===");
        for (int i = 0; i < samples.Count; i++)
        {
            sb.AppendLine($"Q{i + 1}: {samples[i].Query}");
            if (samples[i].ExpectedBehaviors.Length > 0)
                sb.AppendLine($"   Expected aspects: {string.Join(", ", samples[i].ExpectedBehaviors)}");
        }
        sb.AppendLine();
        sb.AppendLine("For each query, consider: does the skill provide the knowledge needed?");
        sb.AppendLine("Does it cover edge cases? Is it actionable?");
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY a JSON object:");
        sb.AppendLine("{\"score\": 0.85, \"strengths\": [\"...\"], \"weaknesses\": [\"...\"]}");
        sb.AppendLine("Score must be between 0.0 and 1.0. Be strict — 0.7 means adequate, 0.9 means excellent.");

        try
        {
            var response = await _judge.GetResponseAsync(
                [new ChatMessage(ChatRole.User, sb.ToString())], cancellationToken: ct).ConfigureAwait(false);
            var text = response.Text ?? "";
            var json = ExtractJson(text);
            if (json == null) return 0.5;

            using var doc = JsonDocument.Parse(json);
            var score = doc.RootElement.GetProperty("score").GetDouble();
            return Math.Clamp(score, 0.0, 1.0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ValidationGate] Judge call failed for {Label}", label);
            return 0.5;
        }
    }

    private void SeedDefaultSamples()
    {
        var defaults = new List<ValidationSample>
        {
            new() { Query = "How should I handle errors gracefully?", ExpectedBehaviors = ["try/catch", "error handling", "recovery"], Category = "general" },
            new() { Query = "What's the best way to structure async code?", ExpectedBehaviors = ["async/await", "Task", "concurrency"], Category = "general" },
            new() { Query = "How do I validate user input?", ExpectedBehaviors = ["validation", "sanitization", "security"], Category = "general" },
        };
        lock (_lock)
        {
            _samples = defaults;
            SaveSamples();
        }
    }

    private void LoadSamples()
    {
        try
        {
            if (File.Exists(_samplesPath))
            {
                var json = File.ReadAllText(_samplesPath);
                var loaded = JsonSerializer.Deserialize<List<ValidationSample>>(json);
                if (loaded != null && loaded.Count > 0)
                    lock (_lock) _samples = loaded;
            }
        }
        catch { /* best-effort load from persistent store */ }
    }

    private void SaveSamples()
    {
        try
        {
            var dir = Path.GetDirectoryName(_samplesPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_samplesPath, JsonSerializer.Serialize(_samples, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort save to persistent store */ }
    }

    private static string? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }
}
