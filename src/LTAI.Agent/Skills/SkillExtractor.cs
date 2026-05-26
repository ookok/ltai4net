using System.Text;
using System.Text.RegularExpressions;
using LTAI.Knowledge.Core;
using LTAI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Skills;

/// <summary>
/// Extracts Skills from successful conversations. This replaces the FixInstinctStore
/// promotion path — when a tool pattern succeeds >=3 times, it becomes a Skill.
/// When a L1 Skill combination succeeds repeatedly, it auto-promotes to L2.
/// </summary>
public sealed class SkillExtractor
{
    private readonly SkillRegistry _registry;
    private readonly IChatClient _llm;
    private readonly ILogger<SkillExtractor> _logger;
    private readonly string _skillsRoot;
    private readonly Dictionary<string, (int Successes, List<string> ToolSequence, string Query, string Response)> _pendingExtractions = new();

    private const int MinSuccessesForL0 = 3;
    private const int MinSuccessesForL1 = 5;
    private const int MinSuccessesForL2 = 10;

    public SkillExtractor(
        SkillRegistry registry,
        IChatClient llm,
        ILogger<SkillExtractor> logger,
        string? skillsRoot = null)
    {
        _registry = registry;
        _llm = llm;
        _logger = logger;
        _skillsRoot = skillsRoot ?? OptionService.Get("paths.skills") ?? Path.Combine(AppContext.BaseDirectory, "skills");
    }

    public void RecordSuccess(string patternKey, List<string> toolSequence, string query, string response)
    {
        if (_pendingExtractions.TryGetValue(patternKey, out var entry))
        {
            _pendingExtractions[patternKey] = (entry.Successes + 1, toolSequence, query, response);
        }
        else
        {
            _pendingExtractions[patternKey] = (1, toolSequence, query, response);
        }

        var (successes, _, _, _) = _pendingExtractions[patternKey];
        var existing = _registry.Get(patternKey);

        if (existing == null && successes >= MinSuccessesForL0)
        {
            _logger.LogInformation("SkillExtractor: {Pattern} reached {Count} successes — triggering L0 skill creation",
                patternKey, successes);

            var capturedQuery = query;
            var capturedResponse = response;
            var capturedTools = toolSequence;
            _ = ExtractAsync(patternKey, "general", CancellationToken.None)
                .ContinueWith(t =>
                {
                    if (t is { IsCompletedSuccessfully: true, Result: not null })
                    {
                        _registry.Register(t.Result);
                        _pendingExtractions.Remove(patternKey, out _);
                        _logger.LogInformation("SkillExtractor: auto-created L0 skill '{Name}'",
                            t.Result.Name);
                    }
                });
        }
        else if (existing != null)
        {
            existing.Evolution.RecordSuccess();
            if (existing.SourceFile != null)
                SkillLoader.SaveEvolution(existing.SourceFile, existing.Evolution);

            var suggestedLayer = _registry.SuggestLayer(existing.Evolution.SuccessRate, existing.Evolution.TotalUses);
            if (suggestedLayer > existing.Layer)
            {
                var promoted = _registry.Promote(existing.Name, suggestedLayer.Value);
                if (promoted != null && existing.SourceFile != null)
                {
                    var newDir = Path.Combine(_skillsRoot, promoted.LayerDir);
                    Directory.CreateDirectory(newDir);
                    var newPath = Path.Combine(newDir, $"{promoted.Name}.md");
                    try
                    {
                        File.Move(existing.SourceFile, newPath, overwrite: false);
                        promoted.SourceFile = newPath;
                        SkillLoader.SaveEvolution(newPath, promoted.Evolution);

                        var oldMeta = existing.SourceFile + ".meta.json";
                        var newMeta = newPath + ".meta.json";
                        if (File.Exists(oldMeta)) File.Move(oldMeta, newMeta, overwrite: false);

                        _logger.LogInformation("SkillExtractor: PROMOTED {Name} {From} → {To} files moved to {Dir} (rate={Rate:F2}, uses={Uses})",
                            promoted.Name, existing.Layer, suggestedLayer.Value,
                            promoted.LayerDir, promoted.Evolution.SuccessRate, promoted.Evolution.TotalUses);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "SkillExtractor: promotion file move failed for {Name}", existing.Name);
                    }
                }
            }
        }
    }

    public void RecordFailure(string patternKey)
    {
        _registry.RecordFailure(patternKey);

        var skill = _registry.Get(patternKey);
        if (skill != null && !skill.IsActive && skill.Evolution.TotalUses >= 20)
        {
            _logger.LogWarning("SkillExtractor: {Name} deactivated — rate={Rate:F2}, uses={Uses}",
                skill.Name, skill.Evolution.SuccessRate, skill.Evolution.TotalUses);
        }
    }

    public async Task<Skill?> ExtractAsync(string patternKey, string domain, CancellationToken ct = default)
    {
        if (!_pendingExtractions.TryGetValue(patternKey, out var entry))
            return null;

        if (entry.Successes < MinSuccessesForL0)
            return null;

        var skill = await GenerateSkillViaLLM(entry, domain, ct).ConfigureAwait(false);
        if (skill == null) return null;

        return await SaveSkillAsync(skill, ct).ConfigureAwait(false);
    }

    public async Task<List<Skill>> ExtractAllReadyAsync(CancellationToken ct = default)
    {
        var ready = _pendingExtractions
            .Where(kv => kv.Value.Successes >= MinSuccessesForL0)
            .Where(kv => _registry.Get(kv.Key) == null)
            .ToList();

        var extracted = new List<Skill>();
        foreach (var (key, _) in ready)
        {
            var skill = await ExtractAsync(key, "general", ct).ConfigureAwait(false);
            if (skill != null)
            {
                extracted.Add(skill);
                _pendingExtractions.Remove(key, out _);
            }
        }

        return extracted;
    }

    private async Task<Skill?> GenerateSkillViaLLM(
        (int Successes, List<string> ToolSequence, string Query, string Response) entry,
        string domain, CancellationToken ct)
    {
        var prompt = $"""
            从以下成功的对话中提炼一个 L0 原子技能 (skill.md 格式):

            用户查询: {Truncate(entry.Query, 200)}
            工具序列: {string.Join(" → ", entry.ToolSequence)}
            成功次数: {entry.Successes}
            领域: {domain}

            生成一个 skill.md，包含:
            - 技能名 (英文、snake_case)
            - 触发模式 (从查询中提取关键正则)
            - 步骤 (工具序列)
            - 验证规则 (一条即可)

            只返回 skill.md 内容，不要其他解释。
            """;

        try
        {
            var response = await _llm.GetResponseAsync(prompt, cancellationToken: ct).ConfigureAwait(false);
            var text = response.Text ?? "";

            if (!text.Contains("skill:") || !text.Contains("triggers:"))
                return null;

            var skillName = ExtractSkillName(text);
            if (string.IsNullOrEmpty(skillName))
            {
                skillName = $"auto_{domain.Replace("/", "_")}_{entry.ToolSequence.Count}tools";
            }

            var tempFile = Path.Combine(Path.GetTempPath(), $"ltai_extract_{skillName}.md");
            await File.WriteAllTextAsync(tempFile, text, ct).ConfigureAwait(false);

            var loader = new SkillLoader(new NullLogger<SkillLoader>());
            var skill = await loader.LoadAsync(tempFile, ct).ConfigureAwait(false);

            try { File.Delete(tempFile); } catch { }

            return skill;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM-based skill extraction failed for {Pattern}", entry.Query);
            return null;
        }
    }

    private async Task<Skill?> SaveSkillAsync(Skill skill, CancellationToken ct)
    {
        var destDir = Path.Combine(_skillsRoot, skill.LayerDir);
        Directory.CreateDirectory(destDir);

        var content = RenderSkillMarkdown(skill);
        var destFile = Path.Combine(destDir, $"{skill.Name}.md");

        await File.WriteAllTextAsync(destFile, content, ct).ConfigureAwait(false);
        SkillLoader.SaveEvolution(destFile, skill.Evolution);

        _logger.LogInformation("SkillExtractor: saved {Name} to {Path}", skill.Name, destFile);
        return skill;
    }

    private static string RenderSkillMarkdown(Skill skill)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# skill: {skill.Name}");
        sb.AppendLine($"domain: {skill.Domain}");
        sb.AppendLine($"layer: {(int)skill.Layer}");
        sb.AppendLine($"version: {skill.Version}");
        sb.AppendLine($"intent: {skill.Intent}");
        sb.AppendLine("triggers:");
        foreach (var t in skill.Triggers)
            sb.AppendLine($"  - pattern: \"{t.Pattern}\"");
        sb.AppendLine("requires:");
        foreach (var r in skill.Requires)
            sb.AppendLine($"  - \"{r}\"");
        if (skill.Confidence < 0.95)
            sb.AppendLine($"confidence: {skill.Confidence:F2}");
        sb.AppendLine();
        sb.AppendLine("## 步骤");
        foreach (var s in skill.Steps)
        {
            var action = s.SkillRef != null ? $"→ {s.SkillRef} {s.Action}" : s.Action;
            sb.AppendLine($"{s.Index}. {action}");
        }
        sb.AppendLine();
        sb.AppendLine("## 验证");
        foreach (var v in skill.Verification)
        {
            if (v.MustContain != null) sb.AppendLine($"- must_contain: \"{v.MustContain}\"");
            if (v.MustNotContain != null) sb.AppendLine($"- must_not_contain: \"{v.MustNotContain}\"");
            if (v.Pattern != null) sb.AppendLine($"- pattern: \"{v.Pattern}\"");
        }

        return sb.ToString();
    }

    private static string ExtractSkillName(string markdown)
    {
        var match = Regex.Match(markdown, @"skill:\s*(\w[\w_]*)");
        return match.Success ? match.Groups[1].Value : "";
    }

    private static string Truncate(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..maxLen] + "...";

    private sealed class NullLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
