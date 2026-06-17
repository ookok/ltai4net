using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Tools;

public sealed record EvalResult
{
    public bool Promoted { get; init; }
    public double Score { get; init; }
    public double? PreviousScore { get; init; }
    public string Reason { get; init; } = "";
    public string StagingPath { get; init; } = "";
    public string ProductionPath { get; init; } = "";
}

public sealed class SkillEvalBenchmark
{
    private readonly string _skillsDir;
    private readonly SkillValidationGate _validationGate;
    private readonly ILogger<SkillEvalBenchmark> _logger;

    private const string StagingDirName = "staging";
    private const string ProductionDirName = "auto-evolved";

    public SkillEvalBenchmark(
        SkillValidationGate validationGate,
        ILogger<SkillEvalBenchmark> logger,
        string skillsDir)
    {
        _validationGate = validationGate;
        _logger = logger;
        _skillsDir = skillsDir;

        Directory.CreateDirectory(GetStagingDir());
        Directory.CreateDirectory(GetProductionDir());
    }

    public async Task<EvalResult> EvaluateAndPromoteAsync(
        string skillName, string content, string? previousContent, CancellationToken ct = default)
    {
        var stagingPath = await WriteToStagingAsync(skillName, content, ct).ConfigureAwait(false);

        var result = await _validationGate.ValidateAsync(skillName, previousContent, content, ct).ConfigureAwait(false);

        if (!result.Accepted)
        {
            _logger.LogInformation(
                "[EvalBenchmark] {Skill} NOT promoted: old={Old:F4} new={New:F4} reason={Reason}",
                skillName, result.OldScore, result.NewScore, result.Reason);

            return new EvalResult
            {
                Promoted = false,
                Score = result.NewScore,
                PreviousScore = result.OldScore,
                Reason = result.Reason,
                StagingPath = stagingPath,
                ProductionPath = GetProductionPath(skillName)
            };
        }

        var prodPath = await PromoteToProductionAsync(skillName, stagingPath, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "[EvalBenchmark] {Skill} PROMOTED: old={Old:F4} new={New:F4} delta={Delta:+0.0000}",
            skillName, result.OldScore, result.NewScore, result.NewScore - result.OldScore);

        return new EvalResult
        {
            Promoted = true,
            Score = result.NewScore,
            PreviousScore = result.OldScore,
            Reason = result.Reason,
            StagingPath = stagingPath,
            ProductionPath = prodPath
        };
    }

    public async Task<string> WriteToStagingAsync(string skillName, string content, CancellationToken ct = default)
    {
        var fileName = SanitizeFileName(skillName) + ".skill.md";
        var path = Path.Combine(GetStagingDir(), fileName);
        Directory.CreateDirectory(GetStagingDir());
        await File.WriteAllTextAsync(path, content, ct).ConfigureAwait(false);
        return path;
    }

    public async Task<string> PromoteToProductionAsync(string skillName, string? stagingPath = null, CancellationToken ct = default)
    {
        var fileName = SanitizeFileName(skillName) + ".skill.md";
        var staging = stagingPath ?? Path.Combine(GetStagingDir(), fileName);
        var production = Path.Combine(GetProductionDir(), fileName);

        if (File.Exists(staging))
        {
            Directory.CreateDirectory(GetProductionDir());
            if (File.Exists(production))
                File.Delete(production);
            File.Move(staging, production);
        }

        var metadata = await ReadMetadataAsync(production, ct).ConfigureAwait(false);
        if (metadata != null)
        {
            metadata["validated_at"] = DateTime.UtcNow.ToString("O");
            metadata["promoted_from"] = "staging";
            await WriteMetadataAsync(production, metadata, ct).ConfigureAwait(false);
        }

        return production;
    }

    public async Task RollbackAsync(string skillName, CancellationToken ct = default)
    {
        var fileName = SanitizeFileName(skillName) + ".skill.md";
        var staging = Path.Combine(GetStagingDir(), fileName);
        var production = Path.Combine(GetProductionDir(), fileName);

        if (File.Exists(staging) && File.Exists(production))
        {
            var backupDir = Path.Combine(_skillsDir, "archived");
            Directory.CreateDirectory(backupDir);
            var backup = Path.Combine(backupDir, $"{fileName}.{DateTime.UtcNow:yyyyMMdd}.rollback");
            File.Move(production, backup);
            File.Move(staging, production);

            _logger.LogInformation("[EvalBenchmark] {Skill} rolled back to staging version", skillName);
        }
    }

    public string? GetStagingContent(string skillName)
    {
        var path = Path.Combine(GetStagingDir(), SanitizeFileName(skillName) + ".skill.md");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public string? GetProductionContent(string skillName)
    {
        var path = Path.Combine(GetProductionDir(), SanitizeFileName(skillName) + ".skill.md");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private string GetStagingDir() => Path.Combine(_skillsDir, StagingDirName);
    private string GetProductionDir() => Path.Combine(_skillsDir, ProductionDirName);
    public string GetProductionPath(string skillName) =>
        Path.Combine(GetProductionDir(), SanitizeFileName(skillName) + ".skill.md");

    private static string SanitizeFileName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9\-]", "-")
            .Trim('-')
            .Replace("--", "-");

    private static async Task<Dictionary<string, string>?> ReadMetadataAsync(string path, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var content = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return BestSkillFormat.ReadMetadata(content);
        }
        catch { return null; }
    }

    private static async Task WriteMetadataAsync(string path, Dictionary<string, string> metadata, CancellationToken ct)
    {
        try
        {
            var content = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var updated = BestSkillFormat.WriteMetadata(content, metadata);
            await File.WriteAllTextAsync(path, updated, ct).ConfigureAwait(false);
        }
        catch { /* best-effort metadata update */ }
    }
}
