using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Tools;

/// <summary>Per-skill statistics for selection optimization.</summary>
public sealed class SkillStat
{
    public int CallCount;
    public int SuccessCount;
    public long TotalLatencyMs;
    public int FailCount;
    public DateTime LastUsed = DateTime.MinValue;
    public double AvgLatencyMs => CallCount > 0 ? (double)TotalLatencyMs / CallCount : 0;
    public double SuccessRate => CallCount > 0 ? (double)SuccessCount / CallCount : 0.5;
}

/// <summary>
/// L1-L3 Skill Evolution Engine with full lifecycle.
/// L1: selection weighting (zero LLM cost)
/// L2: parameter tuning + existing skill updates (LLM-driven, periodic)
/// L3: skill create/update/prune/merge (LLM-driven, periodic)
/// Thread-safe, persisted via filesystem.
///
/// SkillOpt-inspired extensions (v2):
/// - ValidationGate: held-out validation before accepting edits
/// - EditBudget: textual learning rate limiting change per epoch
/// - RejectedBuffer: prevents re-proposing rejected edits
/// - EvalBenchmark: staging→production pipeline with auto-rollback
/// - BestSkillFormat: standardized output with performance metadata
/// </summary>
public sealed class SkillEvolutionEngine
{
    private readonly ConcurrentDictionary<string, SkillStat> _stats = new(StringComparer.OrdinalIgnoreCase);
    private readonly IChatClient _llm;
    private readonly ILogger<SkillEvolutionEngine> _logger;
    private readonly string _skillsDir;
    private int _totalCalls;

    private readonly SkillValidationGate? _validationGate;
    private readonly SkillEditBudget? _editBudget;
    private readonly SkillRejectedBuffer? _rejectedBuffer;
    private readonly SkillEvalBenchmark? _evalBenchmark;

    private const int L2Interval = 500;
    private const int L3Interval = 5000;
    private const int EvalInterval = 1000;
    private const int MaxAutoEvolvedSkills = 50;
    private const int SkillRetentionDays = 90;

    // Track which skills we auto-created for lifecycle management
    private readonly HashSet<string> _autoEvolvedSkills = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _skillsLock = new();

    public SkillEvolutionEngine(
        IChatClient llm,
        ILogger<SkillEvolutionEngine> logger,
        string skillsDir,
        SkillValidationGate? validationGate = null,
        SkillEditBudget? editBudget = null,
        SkillRejectedBuffer? rejectedBuffer = null,
        SkillEvalBenchmark? evalBenchmark = null)
    {
        _llm = llm;
        _logger = logger;
        _skillsDir = skillsDir;
        _validationGate = validationGate;
        _editBudget = editBudget;
        _rejectedBuffer = rejectedBuffer;
        _evalBenchmark = evalBenchmark;

        Directory.CreateDirectory(Path.Combine(skillsDir, "auto-evolved"));
        Directory.CreateDirectory(Path.Combine(skillsDir, "archived"));
        LoadAutoEvolvedRegistry();
    }

    // ═══════════════════════════════════════════
    //  L1: Selection optimization (per-call, zero LLM)
    // ═══════════════════════════════════════════

    /// <summary>Record a tool call outcome. Thread-safe.</summary>
    public void RecordCall(string toolName, bool success, long latencyMs)
    {
        var stat = _stats.GetOrAdd(toolName, _ => new SkillStat());
        Interlocked.Increment(ref stat.CallCount);
        Interlocked.Add(ref stat.TotalLatencyMs, latencyMs);
        Interlocked.Exchange(ref stat.LastUsed, DateTime.UtcNow);
        if (success) Interlocked.Increment(ref stat.SuccessCount);
        else Interlocked.Increment(ref stat.FailCount);
        var calls = Interlocked.Increment(ref _totalCalls);
        if (calls % 100 == 0) PruneStaleSkills();
    }

    /// <summary>Get L1-adjusted RRF score boost for a tool.</summary>
    public double GetRankBoost(string toolName)
    {
        if (!_stats.TryGetValue(toolName, out var stat) || stat.CallCount < 3)
            return 1.0;
        var rate = stat.SuccessRate;
        return rate > 0.6 ? 1.0 + 0.3 * (rate - 0.6) / 0.4
             : rate < 0.4 ? 0.7 + 0.3 * rate / 0.4
             : 1.0;
    }

    /// <summary>Trigger periodic L2/L3/Eval evolution. Call after each RecordCall.</summary>
    public async Task MaybeEvolveAsync(CancellationToken ct = default)
    {
        var calls = Volatile.Read(ref _totalCalls);
        if (calls > 0 && calls % L2Interval == 0)
            await RunL2EvolutionAsync(ct).ConfigureAwait(false);
        if (calls > 0 && calls % EvalInterval == 0 && _evalBenchmark != null)
            await RunEvalBenchmarkAsync(ct).ConfigureAwait(false);
        if (calls > 0 && calls % L3Interval == 0)
            await RunL3EvolutionAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Evaluate staging skills and promote those that pass validation.</summary>
    private async Task RunEvalBenchmarkAsync(CancellationToken ct)
    {
        if (_evalBenchmark == null || _validationGate == null) return;

        var stagingDir = Path.Combine(_skillsDir, "staging");
        if (!Directory.Exists(stagingDir)) return;

        var stagingFiles = Directory.GetFiles(stagingDir, "*.skill.md");
        if (stagingFiles.Length == 0) return;

        _logger.LogInformation("[SkillEvo-Eval] Evaluating {Count} staging skills", stagingFiles.Length);

        foreach (var stagingPath in stagingFiles)
        {
            if (ct.IsCancellationRequested) break;

            var skillName = Path.GetFileNameWithoutExtension(stagingPath);
            var newContent = await File.ReadAllTextAsync(stagingPath, ct).ConfigureAwait(false);
            var oldContent = _evalBenchmark.GetProductionContent(skillName);
            var body = BestSkillFormat.StripMetadata(newContent);

            var result = await _evalBenchmark.EvaluateAndPromoteAsync(
                skillName, newContent, oldContent, ct).ConfigureAwait(false);

            if (!result.Promoted)
            {
                _logger.LogInformation(
                    "[SkillEvo-Eval] {Skill} failed validation (score={Score:F4}), keeping in staging",
                    skillName, result.Score);

                if (_rejectedBuffer != null)
                    _rejectedBuffer.RecordRejection(skillName, body, result.Reason);
            }
        }
    }

    // ═══════════════════════════════════════════
    //  L2: Parameter tuning + skill updates (every 500 calls)
    // ═══════════════════════════════════════════

    private async Task RunL2EvolutionAsync(CancellationToken ct)
    {
        var struggling = _stats
            .Where(kv => kv.Value.CallCount >= 5 && kv.Value.SuccessRate < 0.4)
            .OrderBy(kv => kv.Value.SuccessRate)
            .Take(5)
            .ToList();

        if (struggling.Count == 0)
        {
            // No struggling tools — try updating existing auto-evolved skills instead
            await RefreshExistingSkillsAsync(ct).ConfigureAwait(false);
            return;
        }

        // collect existing skill context
        var existingSkills = ListExistingSkills();
        var existingContext = existingSkills.Count > 0
            ? "\nExisting skills:\n" + string.Join("\n", existingSkills.Select(s => $"  - {s.name} ({s.path})"))
            : "";

        var prompt = new StringBuilder();
        prompt.AppendLine("Analyze these tool call statistics. For each struggling tool, decide whether to:");
        prompt.AppendLine("  a) Suggest parameter/call pattern improvements");
        prompt.AppendLine("  b) Update an existing related skill to cover this tool better");
        prompt.AppendLine("  c) Create a new SKILL.md teaching correct usage");
        prompt.AppendLine(existingContext);
        prompt.AppendLine();
        foreach (var (name, stat) in struggling)
        {
            prompt.AppendLine($"- {name}: successRate={stat.SuccessRate:P1}, calls={stat.CallCount}, avgLatency={stat.AvgLatencyMs:F0}ms");
        }
        prompt.AppendLine();
        prompt.AppendLine("Respond as JSON: {\"actions\":[{\"type\":\"optimize\"|\"update_skill\"|\"create_skill\",");
        prompt.AppendLine("  \"tool\":\"name\",\"existing_skill\":\"if updating\",\"suggestion\":\"...\",\"markdown\":\"...if create/update\"}]}");

        await LlmActionAsync(prompt.ToString(), ct).ConfigureAwait(false);
    }

    private async Task RefreshExistingSkillsAsync(CancellationToken ct)
    {
        var autoSkills = ListAutoEvolvedSkills();
        if (autoSkills.Count == 0) return;

        // Pick the least-used auto-evolved skill for review
        var oldest = autoSkills
            .Select(s => (s, stat: _stats.TryGetValue(Path.GetFileNameWithoutExtension(s.name), out var st) ? st : null))
            .OrderBy(x => x.stat?.LastUsed ?? DateTime.MinValue)
            .FirstOrDefault();

        var prompt = $"Review and improve this auto-evolved skill based on actual usage:\n" +
                     $"Skill: {oldest.s.path}\n" +
                     $"Usage: {(oldest.stat != null ? $"{oldest.stat.CallCount} calls, {oldest.stat.SuccessRate:P1} success" : "never used")}\n\n" +
                     $"Read the file, analyze if it's effective, and output improved markdown.\n" +
                     $"If the skill is obsolete, respond with: {{\"action\":\"delete\",\"reason\":\"...\"}}\n" +
                     $"Otherwise: {{\"action\":\"update\",\"markdown\":\"...\"}}";

        await LlmActionAsync(prompt, ct).ConfigureAwait(false);
    }

    // ═══════════════════════════════════════════
    //  L3: Skill lifecycle (every 5000 calls)
    // ═══════════════════════════════════════════

    private async Task RunL3EvolutionAsync(CancellationToken ct)
    {
        // Phase 1: Prune stale auto-evolved skills
        var pruned = PruneStaleSkills();
        if (pruned > 0)
            _logger.LogInformation("[SkillEvo-L3] Pruned {Count} stale skills", pruned);

        // Phase 2: Detect overlap and merge candidates
        await DetectAndMergeSkillsAsync(ct).ConfigureAwait(false);

        // Phase 3: Create new skills for capability gaps
        await CreateNewSkillsAsync(ct).ConfigureAwait(false);
    }

    private async Task DetectAndMergeSkillsAsync(CancellationToken ct)
    {
        var autoSkills = ListAutoEvolvedSkills();
        if (autoSkills.Count < 2) return;

        var prompt = new StringBuilder();
        prompt.AppendLine("Review these auto-evolved skills for potential merging or removal:");
        prompt.AppendLine();
        foreach (var s in autoSkills)
        {
            var stat = _stats.TryGetValue(Path.GetFileNameWithoutExtension(s.name), out var sv) ? sv : null;
            var usage = stat != null ? $"{stat.CallCount}calls/{stat.SuccessRate:P1}" : "no data";
            prompt.AppendLine($"- {s.name} ({usage})");
        }
        prompt.AppendLine();
        prompt.AppendLine("For each, decide: keep, merge (with which), or delete.");
        prompt.AppendLine("Respond as JSON: {\"actions\":[{\"skill\":\"...\",\"action\":\"keep\"|\"delete\"|\"merge\",");
        prompt.AppendLine("  \"merge_into\":\"...\",\"reason\":\"...\",\"merged_markdown\":\"...if merge\"}]}");

        await LlmActionAsync(prompt.ToString(), ct).ConfigureAwait(false);
    }

    private async Task CreateNewSkillsAsync(CancellationToken ct)
    {
        // Enforce cap
        if (_autoEvolvedSkills.Count >= MaxAutoEvolvedSkills)
        {
            _logger.LogInformation("[SkillEvo-L3] Max auto-evolved skills reached ({Count}), skipping creation", MaxAutoEvolvedSkills);
            return;
        }

        // Find pain points or capability gaps
        var painPoints = _stats
            .Where(kv => kv.Value.CallCount >= 10 && kv.Value.SuccessRate < 0.3)
            .OrderBy(kv => kv.Value.SuccessRate)
            .Take(3)
            .ToList();

        var existing = ListExistingSkills();
        var existingNames = new HashSet<string>(existing.Select(s => s.name.ToLowerInvariant()));

        if (painPoints.Count == 0)
        {
            // Look for gap: tools often used together
            var topTools = _stats
                .Where(kv => kv.Value.CallCount >= 20 && kv.Value.SuccessRate > 0.8)
                .OrderByDescending(kv => kv.Value.CallCount)
                .Take(3)
                .ToList();
            if (topTools.Count == 0) return;

            var gapPrompt = new StringBuilder();
            gapPrompt.AppendLine("Frequently used tools that may indicate missing higher-level skills:");
            gapPrompt.AppendLine("Tools: " + string.Join(", ", topTools.Select(t => $"{t.Key}({t.Value.CallCount}x)")));
            gapPrompt.AppendLine("Existing skills: " + string.Join(", ", existingNames));
            gapPrompt.AppendLine();
            gapPrompt.AppendLine("Suggest new skill names that DON'T conflict with existing ones.");
            gapPrompt.AppendLine("Respond as JSON: {\"skills\":[{\"name\":\"new-unique-name\",\"description\":\"...\",\"markdown\":\"...\"}]}");

            await LlmActionAsync(gapPrompt.ToString(), ct).ConfigureAwait(false);
            return;
        }

        var createPrompt = new StringBuilder();
        createPrompt.AppendLine("Create SKILL.md files for struggling tools. Each skill teaches the LLM correct usage patterns.");
        createPrompt.AppendLine("Existing skills (AVOID name conflicts): " + string.Join(", ", existingNames));
        createPrompt.AppendLine();
        foreach (var (name, stat) in painPoints)
        {
            createPrompt.AppendLine($"- Tool {name}: successRate={stat.SuccessRate:P1}, calls={stat.CallCount}");
        }
        createPrompt.AppendLine();
        createPrompt.AppendLine("Respond as JSON: {\"skills\":[{\"name\":\"unique-name\",\"description\":\"...\",\"markdown\":\"...SKILL.md content...\"}]}");
        createPrompt.AppendLine("Names must be lowercase-kebab-case and NOT match any existing skill name.");

        await LlmActionAsync(createPrompt.ToString(), ct).ConfigureAwait(false);
    }

    // ═══════════════════════════════════════════
    //  Shared LLM action dispatcher
    //  (SkillOpt-inspired: ValidationGate + EditBudget + RejectedBuffer + EvalBenchmark)
    // ═══════════════════════════════════════════

    private async Task LlmActionAsync(string prompt, CancellationToken ct)
    {
        string raw;
        try
        {
            var response = await _llm.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct).ConfigureAwait(false);
            raw = response.Text ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SkillEvo] LLM call failed");
            return;
        }

        var json = ExtractJson(raw);
        if (json == null) return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Handle actions array (L2 optimize/update/create, L3 merge)
            if (root.TryGetProperty("actions", out var actions))
            {
                foreach (var a in actions.EnumerateArray())
                {
                    var type = a.GetProperty("type").GetString()
                             ?? a.GetProperty("action").GetString()
                             ?? "keep";
                    var skillName = a.TryGetProperty("tool", out var t)
                        ? t.GetString() ?? ""
                        : a.GetProperty("skill").GetString() ?? "";
                    var markdown = a.TryGetProperty("markdown", out var md) ? md.GetString() : null;
                    var mergedMd = a.TryGetProperty("merged_markdown", out var mm) ? mm.GetString() : null;
                    var mergeInto = a.TryGetProperty("merge_into", out var mi) ? mi.GetString() : null;
                    var reason = a.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";

                    switch (type)
                    {
                        case "delete":
                            await DeleteSkillFileAsync(skillName, reason, ct).ConfigureAwait(false);
                            break;
                        case "merge" when mergeInto != null && mergedMd != null:
                            await MergeSkillFilesAsync(skillName, mergeInto, mergedMd, ct).ConfigureAwait(false);
                            break;
                        case "update_skill":
                        case "update" when markdown != null:
                            await ValidatedWriteAsync(skillName, "", markdown!, false, ct).ConfigureAwait(false);
                            break;
                        case "create_skill":
                        case "create" when markdown != null:
                            await ValidatedWriteAsync(skillName, "", markdown!, true, ct).ConfigureAwait(false);
                            break;
                    }
                }
            }

            // Handle skills array (L3 creation)
            if (root.TryGetProperty("skills", out var skills))
            {
                foreach (var s in skills.EnumerateArray())
                {
                    var name = s.GetProperty("name").GetString() ?? Guid.NewGuid().ToString("N")[..8];
                    var description = s.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                    var markdown = s.TryGetProperty("markdown", out var m) ? m.GetString() : null;
                    if (markdown != null)
                        await ValidatedWriteAsync(name, description, markdown, true, ct).ConfigureAwait(false);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[SkillEvo] Failed to parse LLM response: {Raw}", raw);
        }
    }

    /// <summary>
    /// SkillOpt-inspired validated write pipeline:
    /// 1. RejectedBuffer check — skip if this content was previously rejected
    /// 2. EditBudget check — skip if budget exhausted for this epoch
    /// 3. BestSkillFormat — standardize output with performance metadata
    /// 4. ValidationGate — held-out validation before acceptance
    /// 5. EvalBenchmark — write to staging, evaluate, promote if passed
    /// Fallback to direct write if any component is null.
    /// </summary>
    private async Task ValidatedWriteAsync(
        string name, string description, string markdown, bool isNew, CancellationToken ct)
    {
        var oldContent = !isNew ? GetExistingContent(name) : null;

        // Step 1: RejectedBuffer — skip previously rejected content
        if (_rejectedBuffer != null && _rejectedBuffer.WasRejected(name, markdown))
        {
            _logger.LogInformation("[SkillEvo] Skipped {Name}: content was previously rejected", name);
            return;
        }

        // Step 2: EditBudget — check if we have budget for this change
        if (_editBudget != null && !_editBudget.TrySpend(name, oldContent ?? "", markdown))
        {
            _logger.LogInformation("[SkillEvo] Budget exhausted for {Name}, skipping edit", name);
            return;
        }

        // Step 3: BestSkillFormat — standardize with metadata
        var accuracy = _stats.TryGetValue(name, out var stat) ? stat.SuccessRate : (double?)null;
        var editCount = _editBudget?.GetEditCount(name);
        var epoch = editCount.HasValue ? editCount / 10 + 1 : 1;
        var body = BestSkillFormat.StripMetadata(markdown);
        var standardized = BestSkillFormat.Build(
            name, description, body,
            accuracy: accuracy,
            editCount: editCount,
            epoch: epoch);

        // Step 4+5: ValidationGate + EvalBenchmark pipeline
        if (_evalBenchmark != null && _validationGate != null)
        {
            var result = await _evalBenchmark.EvaluateAndPromoteAsync(
                name, standardized, oldContent, ct).ConfigureAwait(false);

            if (result.Promoted)
            {
                lock (_skillsLock) { _autoEvolvedSkills.Add(SanitizeFileName(name) + ".skill.md"); }
                SaveAutoEvolvedRegistry();
                _logger.LogInformation("[SkillEvo] Validated and promoted: {Name} (score={Score:F4})",
                    name, result.Score);

                // Update metadata with validation score
                var prodContent = _evalBenchmark.GetProductionContent(name);
                if (prodContent != null)
                {
                    var withMeta = BestSkillFormat.UpdateMetadata(prodContent, "validation_score",
                        result.Score.ToString("F4"));
                    withMeta = BestSkillFormat.UpdateMetadata(withMeta, "validated_at",
                        DateTime.UtcNow.ToString("O"));
                    withMeta = BestSkillFormat.UpdateMetadata(withMeta, "promoted",
                        result.Promoted.ToString().ToLowerInvariant());
                    await File.WriteAllTextAsync(
                        _evalBenchmark.GetProductionPath(name) ?? Path.Combine(_skillsDir, "auto-evolved", SanitizeFileName(name) + ".skill.md"),
                        withMeta, ct).ConfigureAwait(false);
                }
            }
            else
            {
                _logger.LogInformation("[SkillEvo] {Name} failed validation (score={Score:F4}), kept in staging",
                    name, result.Score);
                if (_rejectedBuffer != null)
                    _rejectedBuffer.RecordRejection(name, body, result.Reason);
            }
        }
        else
        {
            // No validation gate — direct write (legacy path)
            await WriteSkillFileAsync(name, description, standardized, ct).ConfigureAwait(false);
        }
    }

    private string? GetExistingContent(string skillName)
    {
        var path = Path.Combine(_skillsDir, "auto-evolved", SanitizeFileName(skillName) + ".skill.md");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    // ═══════════════════════════════════════════
    //  File lifecycle operations
    // ═══════════════════════════════════════════

    private async Task WriteSkillFileAsync(string name, string description, string markdown, CancellationToken ct)
    {
        try
        {
            var dir = Path.Combine(_skillsDir, "auto-evolved");
            Directory.CreateDirectory(dir);
            var fileName = SanitizeFileName(name) + ".skill.md";
            var path = Path.Combine(dir, fileName);

            var content = markdown.StartsWith("---")
                ? markdown
                : $"""
                ---
                name: {name}
                description: {description}
                allowedTools: []
                ---

                {markdown}
                """;

            // If file exists, this is an update; log accordingly
            var exists = File.Exists(path);
            await File.WriteAllTextAsync(path, content, ct).ConfigureAwait(false);

            lock (_skillsLock) { _autoEvolvedSkills.Add(fileName); }
            SaveAutoEvolvedRegistry();
            _logger.LogInformation("[SkillEvo] {Action} skill: {Path}",
                exists ? "Updated" : "Created", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SkillEvo] Failed to write skill file");
        }
    }

    private async Task DeleteSkillFileAsync(string? name, string reason, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(name)) return;
        try
        {
            var dir = Path.Combine(_skillsDir, "auto-evolved");
            var fileName = SanitizeFileName(name) + ".skill.md";
            var path = Path.Combine(dir, fileName);
            var archiveDir = Path.Combine(_skillsDir, "archived");
            Directory.CreateDirectory(archiveDir);
            var archivePath = Path.Combine(archiveDir, $"{fileName}.{DateTime.UtcNow:yyyyMMdd}.bak");
            if (File.Exists(path))
            {
                File.Move(path, archivePath);
                lock (_skillsLock) { _autoEvolvedSkills.Remove(fileName); }
                SaveAutoEvolvedRegistry();
                await File.AppendAllTextAsync(
                    Path.Combine(archiveDir, "deletion_log.txt"),
                    $"[{DateTime.UtcNow:O}] Deleted {fileName}: {reason}\n", ct).ConfigureAwait(false);
                _logger.LogInformation("[SkillEvo] Deleted skill: {Name} - {Reason}", name, reason);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SkillEvo] Failed to delete skill: {Name}", name);
        }
    }

    private async Task MergeSkillFilesAsync(string? sourceName, string? targetName, string mergedMarkdown, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sourceName) || string.IsNullOrEmpty(targetName)) return;
        // Write merged content to target, then delete source
        await WriteSkillFileAsync(targetName, "", mergedMarkdown, ct).ConfigureAwait(false);
        await DeleteSkillFileAsync(sourceName, $"Merged into {targetName}", ct).ConfigureAwait(false);
    }

    private int PruneStaleSkills()
    {
        var autoSkills = ListAutoEvolvedSkills();
        if (autoSkills.Count == 0) return 0;

        var cutoff = DateTime.UtcNow.AddDays(-SkillRetentionDays);
        int pruned = 0;

        foreach (var s in autoSkills)
        {
            var stat = _stats.TryGetValue(Path.GetFileNameWithoutExtension(s.name), out var sv) ? sv : null;
            if (stat == null && (DateTime.UtcNow - File.GetLastWriteTime(s.path)).TotalDays > SkillRetentionDays)
            {
                // Never used and old — archive
                var archiveDir = Path.Combine(_skillsDir, "archived");
                Directory.CreateDirectory(archiveDir);
                var archivePath = Path.Combine(archiveDir, $"{s.name}.{DateTime.UtcNow:yyyyMMdd}.stale");
                File.Move(s.path, archivePath);
                lock (_skillsLock) { _autoEvolvedSkills.Remove(s.name); }
                pruned++;
            }
            else if (stat != null && stat.LastUsed < cutoff && stat.CallCount < 5)
            {
                File.Delete(s.path);
                lock (_skillsLock) { _autoEvolvedSkills.Remove(s.name); }
                pruned++;
            }
        }

        if (pruned > 0) SaveAutoEvolvedRegistry();
        return pruned;
    }

    // ═══════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════

    private static string? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private static string SanitizeFileName(string name) =>
        Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9\-]", "-")
            .Trim('-')
            .Replace("--", "-");

    private List<(string name, string path)> ListAutoEvolvedSkills()
    {
        var dir = Path.Combine(_skillsDir, "auto-evolved");
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, "*.skill.md")
            .Select(p => (Path.GetFileNameWithoutExtension(p), p))
            .ToList();
    }

    private List<(string name, string path)> ListExistingSkills()
    {
        var results = new List<(string, string)>();
        foreach (var dir in Directory.GetDirectories(_skillsDir))
        {
            foreach (var f in Directory.GetFiles(dir, "*.md"))
                results.Add((Path.GetFileNameWithoutExtension(f), f));
        }
        foreach (var f in Directory.GetFiles(_skillsDir, "*.md"))
            results.Add((Path.GetFileNameWithoutExtension(f), f));
        return results;
    }

    private void LoadAutoEvolvedRegistry()
    {
        var path = Path.Combine(_skillsDir, "auto-evolved", ".registry");
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadLines(path))
        {
            if (!string.IsNullOrWhiteSpace(line))
                lock (_skillsLock) { _autoEvolvedSkills.Add(line.Trim()); }
        }
    }

    private void SaveAutoEvolvedRegistry()
    {
        var dir = Path.Combine(_skillsDir, "auto-evolved");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".registry"), string.Join("\n", _autoEvolvedSkills));
    }
}
