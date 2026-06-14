using System.Text.RegularExpressions;
using LTAI.AI;
using LTAI.Core.Safety;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

/// <summary>
/// Auto-extracts structured facts/preferences from conversation and persists them
/// to <see cref="PalaceStore"/>. Runs after each completed AI turn so the agent
/// doesn't need to explicitly call <c>Remember</c>.
/// 
/// Fallback: when 12 regex patterns yield 0 matches, a lightweight L3 LLM call
/// performs a single-pass classification to catch unstructured facts the regex
/// patterns miss (e.g., implicit preferences, contextual facts).
/// </summary>
public sealed partial class MemoryExtractor
{
    private readonly PalaceStore _store;
    private readonly MultiGraphStore? _multiGraph;
    private readonly FactExtractor? _factExtractor;
    private readonly IChatClient? _l3Fallback;
    private readonly ILogger<MemoryExtractor>? _logger;

    private static readonly string[] TechKeywords =
    [
        "api", "key", "token", "config", "password", "url", "version",
        "package", "dependency", "database", "server", "deploy",
    ];

    // ── Core extraction patterns ──

    // Explicit save: "记住: X", "记得 X", "保存 X"
    [GeneratedRegex(@"(?:记住|记得|remember|save|保存)\s*
        (?:[:：])?\s*(.+)", RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled)]
    private static partial Regex ExplicitSavePattern();

    // User preference: "我(用/喜欢/是/在/有/做/开发/使用) X", "I(use/like/am/work on/prefer) X"
    [GeneratedRegex(@"(?:我|I)\s*(?:用|使用|喜欢|prefer|use|like|am|work\s+on)\s*
        (?:的是\s*)?([^，。.!?；;\n]{2,60})", RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled)]
    private static partial Regex UserPreferencePattern();

    // "我的 X 是 Y"
    [GeneratedRegex(@"我的?([^\s，。,!?；;]{1,20})(?:是|为)\s*([^，。.!?；;\n]{2,60})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MyAttributePattern();

    // Project facts: "本项目使用/采用/基于 X"
    [GeneratedRegex(@"(?:本项目|本工程|这个项目|the\s+project|this\s+project)\s*
        (?:使用|采用|基于|用|is|uses|built|based)\s*
        ([^，。.!?；;\n]{2,60})", RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled)]
    private static partial Regex ProjectFactPattern();

    // ── Enhanced extraction patterns (new) ──

    // Dislikes: "我(不喜欢/讨厌/不用) X"
    [GeneratedRegex(@"(?:我|I)\s*(?:不(?:喜欢|用|想|推荐|建议|要)|hate|dislike|don'?t\s+(?:like|use|want|recommend))\s*
        ([^，。.!?；;\n]{2,60})", RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled)]
    private static partial Regex DislikePattern();

        // Task/issue mention: "遇到(问题/错误/bug/异常) X", "发现 Y"
        [GeneratedRegex(@"(?:遇到|发现|出现|碰到|find|found|encounter|discover|出现)\s*
        (?:了\s*)?(?:问题|错误|bug|异常|issue|error)\s*
        (?:[:：])?\s*([^，。.!?；;\n]{4,100})", RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled)]
    private static partial Regex IssuePattern();

    // Technology/tool mention: "用(了) X", "正在用 X", "切换(到) X"
    [GeneratedRegex(@"(?:使用|用了|正在用|切换|migrate|migrated|migration|迁移)\s*
        (?:到|至|from|to|到\s)?\s*(?:过\s*)?([^\s，。.!?；;\n]{2,40})",
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled)]
    private static partial Regex ToolMigrationPattern();

    // Goal/intention: "我(想/要/打算/计划) X"
    [GeneratedRegex(@"(?:我|I|we|我们)\s*(?:想|要|打算|计划|will|plan|intend|going\s+to)\s*
        ([^，。.!?；;\n]{4,100})", RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled)]
    private static partial Regex GoalPattern();

    // Decision/rationale: "因为...所以...", "原因(是) X"
    [GeneratedRegex(@"(?:因为|由于|原因(?:是)?|because|reason|since)\s*
        ([^，。.!?；;\n]{4,100})", RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled)]
    private static partial Regex ReasonPattern();

    // Constraint/requirement: "需要 X", "必须 X", "要求 X", "依赖 X"
    [GeneratedRegex(@"(?:需要|必须|要求|依赖|requires|needs|must|depends?\s+on)\s*
        ([^，。.!?；;\n]{4,80})", RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled)]
    private static partial Regex RequirementPattern();

    // Comparison: "X 比 Y 好/快/稳定"
    [GeneratedRegex(@"([^\s，。.!?；;\n]{2,30})\s*(?:比|vs?\.|versus|compared\s+to)\s*
        ([^\s，。.!?；;\n]{2,30})\s*(?:好|快|稳定|简单|快|慢|cheap|expensive|fast|slow|better|worse)",
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled)]
    private static partial Regex ComparisonPattern();

    // Workflow/procedure: "先...然后...", "流程(是) X"
    [GeneratedRegex(@"(?:工作流|流程|步骤|workflow|process|procedure|steps|how\s+to)\s*
        (?:[:：])?\s*([^。.!?；;\n]{8,200})", RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled)]
    private static partial Regex WorkflowPattern();

    /// <summary>
    /// Compute dynamic importance score for extracted memory content.
    /// Factors: length (longer = more detailed = more important),
    /// specificity (contains numbers/names = more important),
    /// recency signals (contains time references).
    /// Returns value in [0.2, 0.95] clamped from base.
    /// </summary>
    private static double ComputeImportance(string content, double baseImportance)
    {
        if (string.IsNullOrWhiteSpace(content)) return baseImportance;
        var score = baseImportance;

        // Length factor: longer memories tend to be more important
        if (content.Length > 80) score += 0.1;
        if (content.Length > 150) score += 0.05;

        // Specificity: contains numbers, paths, or technical names
        if (content.Any(char.IsDigit)) score += 0.05;
        if (content.Contains('/') || content.Contains('\\')) score += 0.05;
        if (content.Contains('.') && content.Any(char.IsUpper)) score += 0.05;

        // Technical keywords suggest higher importance
        if (TechKeywords.Any(k => content.Contains(k, StringComparison.OrdinalIgnoreCase)))
            score += 0.1;

        return Math.Clamp(score, 0.2, 0.95);
    }

    public MemoryExtractor(PalaceStore store, FactExtractor? factExtractor = null,
        ILogger<MemoryExtractor>? logger = null, MultiGraphStore? multiGraph = null,
        IChatClient? l3Fallback = null)
    {
        _store = store;
        _factExtractor = factExtractor;
        _logger = logger;
        _multiGraph = multiGraph;
        _l3Fallback = l3Fallback;
    }

    /// <summary>Extract facts from the latest user message and store as memories.</summary>
    public async Task<int> ExtractFromTurnAsync(string userMessage, string? entityId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage)) return 0;

        var extracted = new List<(string wing, string room, string content, double importance)>();

        // 1. Explicit save: "记住: X"
        foreach (Match m in ExplicitSavePattern().Matches(userMessage))
        {
            var text = m.Groups[1].Value.Trim();
            if (text.Length >= 3 && text.Length <= 200)
                extracted.Add(("user", "saved_note", text, ComputeImportance(text, 0.7)));
        }

        // 2. User preference: "我用/喜欢 X"
        foreach (Match m in UserPreferencePattern().Matches(userMessage))
        {
            var text = m.Groups[1].Value.Trim();
            if (text.Length >= 3 && text.Length <= 100)
                extracted.Add(("user", "preference", text, ComputeImportance(text, 0.6)));
        }

        // 3. "我的 X 是 Y"
        foreach (Match m in MyAttributePattern().Matches(userMessage))
        {
            var attr = m.Groups[1].Value.Trim();
            var val = m.Groups[2].Value.Trim();
            if (attr.Length >= 1 && val.Length >= 1 && val.Length <= 80)
                extracted.Add(("user", $"attr_{attr}", $"{attr}: {val}", ComputeImportance(val, 0.5)));
        }

        // 4. Project facts: "本项目使用 X"
        foreach (Match m in ProjectFactPattern().Matches(userMessage))
        {
            var text = m.Groups[1].Value.Trim();
            if (text.Length >= 3 && text.Length <= 100)
                extracted.Add(("project", "tech_stack", text, ComputeImportance(text, 0.6)));
        }

        // 5. Dislikes: "我不喜欢 X"
        foreach (Match m in DislikePattern().Matches(userMessage))
        {
            var text = m.Groups[1].Value.Trim();
            if (text.Length >= 3 && text.Length <= 100)
                extracted.Add(("user", "dislike", text, ComputeImportance(text, 0.5)));
        }

        // 6. Issue detection: "遇到问题 X"
        foreach (Match m in IssuePattern().Matches(userMessage))
        {
            var text = m.Groups[1].Value.Trim();
            if (text.Length >= 4 && text.Length <= 200)
                extracted.Add(("project", "issue", text, ComputeImportance(text, 0.55)));
        }

        // 7. Tool migration: "切换到 X"
        foreach (Match m in ToolMigrationPattern().Matches(userMessage))
        {
            var text = m.Groups[1].Value.Trim();
            if (text.Length >= 2 && text.Length <= 40)
                extracted.Add(("project", "tool", text, ComputeImportance(text, 0.45)));
        }

        // 8. Goal/intention: "我想 X"
        foreach (Match m in GoalPattern().Matches(userMessage))
        {
            var text = m.Groups[1].Value.Trim();
            if (text.Length >= 4 && text.Length <= 200)
                extracted.Add(("user", "goal", text, ComputeImportance(text, 0.5)));
        }

        // 9. Reason/decision: "因为 X"
        foreach (Match m in ReasonPattern().Matches(userMessage))
        {
            var text = m.Groups[1].Value.Trim();
            if (text.Length >= 4 && text.Length <= 200)
                extracted.Add(("project", "decision", text, ComputeImportance(text, 0.6)));
        }

        // 10. Requirement/constraint: "需要 X"
        foreach (Match m in RequirementPattern().Matches(userMessage))
        {
            var text = m.Groups[1].Value.Trim();
            if (text.Length >= 4 && text.Length <= 100)
                extracted.Add(("project", "requirement", text, ComputeImportance(text, 0.55)));
        }

        // 11. Comparison: "X 比 Y 好"
        foreach (Match m in ComparisonPattern().Matches(userMessage))
        {
            var subject = m.Groups[1].Value.Trim();
            var compared = m.Groups[2].Value.Trim();
            if (subject.Length >= 2 && compared.Length >= 2)
                extracted.Add(("project", "comparison", $"{subject} > {compared}", ComputeImportance($"{subject} {compared}", 0.4)));
        }

        // 12. Workflow: "流程是 X"
        foreach (Match m in WorkflowPattern().Matches(userMessage))
        {
            var text = m.Groups[1].Value.Trim();
            if (text.Length >= 8 && text.Length <= 300)
                extracted.Add(("project", "workflow", text, ComputeImportance(text, 0.5)));
        }

        if (extracted.Count == 0)
        {
            var llmExtracted = await LlmFallbackExtractAsync(userMessage, entityId).ConfigureAwait(false);
            if (llmExtracted > 0) return llmExtracted;
            return 0;
        }

        var meta = entityId != null
            ? new Dictionary<string, object> { ["entity_id"] = entityId }
            : null;

        int stored = 0;
        foreach (var (wing, room, content, importance) in extracted)
        {
            if (!SafetyRules.IsSafeByRules(content)) continue;

            try
            {
                // AnchorMem-inspired: extract atomic facts as retrieval anchors.
                // Facts are appended to content so FTS5 indexes them; original
                // content remains intact for generation context.
                var facts = _factExtractor != null
                    ? await _factExtractor.ExtractFactsAsync(content, CancellationToken.None).ConfigureAwait(false)
                    : (IReadOnlyList<string>)[];

                var augmentedContent = content;
                var factMeta = meta != null ? new Dictionary<string, object>(meta) : new Dictionary<string, object>();
                if (facts.Count > 0)
                {
                    augmentedContent = $"{content}\n[facts]: {string.Join("; ", facts)}";
                    factMeta["facts"] = facts.ToArray();
                }

                await _store.StoreAsync(wing, room, augmentedContent,
                    role: "user",
                    importance: importance,
                    agentId: "extractor",
                    metadata: factMeta.Count > 0 ? factMeta : null,
                    ttlMs: PalaceStore.DefaultTtlMs).ConfigureAwait(false);

                // Fast Path: also write to MultiGraphStore for intent-aware retrieval
                if (_multiGraph != null)
                {
                    var nodeId = $"{wing}:{room}:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
                    _multiGraph.StoreNode(nodeId, wing, augmentedContent);
                }

                _logger?.LogDebug("MemoryExtractor: stored '{Room}' (wing={Wing}){Facts}",
                    room, wing, facts.Count > 0 ? $" +{facts.Count} facts" : "");
                stored++;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "MemoryExtractor: failed to store '{Room}'", room);
            }
        }

        if (stored > 0)
            _logger?.LogInformation("MemoryExtractor: extracted {Stored}/{Extracted} facts from turn", stored, extracted.Count);

        return stored;
    }

    /// <summary>
    /// Lightweight LLM fallback: when 12 regex patterns produce 0 matches, use a
    /// single-pass L3 LLM call to classify the user message for implicit facts.
    /// Token-constrained (max 50 output tokens) to minimize cost.
    /// </summary>
    private async Task<int> LlmFallbackExtractAsync(string userMessage, string? entityId)
    {
        if (_l3Fallback == null || userMessage.Length < 10 || userMessage.Length > 2000)
            return 0;

        try
        {
            var prompt = $$"""
                Classify this user message into one category. Reply with ONLY the category name and a 1-sentence summary, separated by ": ".
                Categories: preference, goal, requirement, project_fact, issue, dislike, workflow, none.
                
                Message: {{userMessage}}
                """;

            var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };
            var response = await _l3Fallback.GetResponseAsync(
                messages,
                new ChatOptions { Temperature = 0f, MaxOutputTokens = 50 },
                CancellationToken.None).ConfigureAwait(false);

            var text = response.Text?.Trim();
            if (string.IsNullOrEmpty(text) || text.StartsWith("none", StringComparison.OrdinalIgnoreCase))
                return 0;

            var colonIdx = text.IndexOf(':');
            var category = colonIdx > 0 ? text[..colonIdx].Trim().ToLowerInvariant() : text.ToLowerInvariant();
            var summary = colonIdx > 0 ? text[(colonIdx + 1)..].Trim() : text;
            if (summary.Length < 3) return 0;

            var wing = category switch
            {
                "preference" or "dislike" => "user",
                "goal" or "requirement" => "user",
                _ => "project"
            };
            var room = category switch
            {
                "preference" => "preference",
                "dislike" => "dislike",
                "goal" => "goal",
                "requirement" => "requirement",
                "project_fact" => "tech_stack",
                "issue" => "issue",
                "workflow" => "workflow",
                _ => "extracted"
            };

            if (!SafetyRules.IsSafeByRules(summary)) return 0;

            var facts = _factExtractor != null
                ? await _factExtractor.ExtractFactsAsync(summary, CancellationToken.None).ConfigureAwait(false)
                : (IReadOnlyList<string>)[];

            var augmentedContent = summary;
            var factMeta = entityId != null
                ? new Dictionary<string, object> { ["entity_id"] = entityId }
                : new Dictionary<string, object>();
            if (facts.Count > 0)
            {
                augmentedContent = $"{summary}\n[facts]: {string.Join("; ", facts)}";
                factMeta["facts"] = facts.ToArray();
            }

            await _store.StoreAsync(wing, room, augmentedContent,
                role: "user",
                importance: ComputeImportance(summary, 0.4),
                agentId: "extractor-llm",
                metadata: factMeta.Count > 0 ? factMeta : null,
                ttlMs: PalaceStore.DefaultTtlMs).ConfigureAwait(false);

            _logger?.LogDebug("MemoryExtractor LLM fallback: stored '{Category}' → {Wing}/{Room}", category, wing, room);
            return 1;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "MemoryExtractor: LLM fallback failed (non-fatal)");
            return 0;
        }
    }
}
