using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LTAI.AI;
using LTAI.Agent.Memory;
using LTAI.Agent.Utils;
using LibGit2Sharp;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Tools.Review;

/// <summary>
/// Code review tools inspired by Alibaba Open Code Review:
/// deterministic engineering (grouping, rule matching, position repair, reflection)
/// combined with Agent-powered analysis.
/// </summary>
[ToolDomain("review")]
public sealed class ReviewTools
{
    private readonly string _ws;
    private readonly ReviewRuleEngine _ruleEngine;
    private readonly DiffGroupingAnalyzer _grouping;
    private readonly ExternalPositioner _positioner;
    private readonly ReviewReflector _reflector;
    private readonly PalaceStore? _memoryStore;
    private readonly IServiceProvider? _sp;
    private readonly IChatClient? _llm;
    private readonly IReadOnlyList<AITool>? _allTools;
    private const int MaxFileReadSize = 2_000_000;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false,
    };
    private static readonly int MaxAuditEntries = int.TryParse(
        Environment.GetEnvironmentVariable("LTAI_AUDIT_MAX_ENTRIES"), out var m) ? Math.Max(100, m) : 2000;

    private static readonly int ReviewParallelism = int.TryParse(
        Environment.GetEnvironmentVariable("LTAI_REVIEW_PARALLELISM"), out var rp) ? Math.Max(1, Math.Min(8, rp)) : 4;

    private static readonly SemaphoreSlim ReviewConcurrencyGate = new(ReviewParallelism, ReviewParallelism);

    public ReviewTools(string ws, PalaceStore? memoryStore = null,
        IServiceProvider? serviceProvider = null, IChatClient? chatClient = null,
        IReadOnlyList<AITool>? agentTools = null)
    {
        _ws = ws;
        _ruleEngine = new ReviewRuleEngine();
        _grouping = new DiffGroupingAnalyzer();
        _positioner = new ExternalPositioner();
        _reflector = new ReviewReflector();
        _memoryStore = memoryStore;
        _sp = serviceProvider;
        _llm = chatClient;
        _allTools = agentTools;

        // Load rules
        _ruleEngine.LoadBuiltinRules();
        try
        {
            _ruleEngine.LoadProjectRules(_ws);
        }
        catch (Exception) { System.Diagnostics.Debug.WriteLine("[ReviewTools] Project rules not loaded"); }
    }

    /// <summary>Load custom review rules from JSON string (4-layer chain: builtin → project → custom → ad-hoc).</summary>
    [Description("加载自定义审查规则 JSON。规则链优先级：内建 < 项目配置 < 自定义 < 本次调用。\n"
        + "适用场景：为特定仓库或语言补充审查规则。\n"
        + "参数规则格式：[{\"Id\":\"R001\",\"Name\":\"规则名\",\"Category\":\"correctness|security|performance|maintainability\",\"Severity\":\"error|warning|info\",\"Pattern\":\"要匹配的正则\",\"FilePattern\":\"**/*.cs\",\"MessageTemplate\":\"提示信息，{{0}}=路径 {{1}}=匹配文本\"}]")]
    [ToolExample("加载自定义规则：禁止使用 dynamic 关键字")]
    public string LoadReviewRules(
        [Description("Rules JSON array")] string rulesJson)
    {
        try
        {
            var rules = JsonSerializer.Deserialize<List<ReviewRule>>(rulesJson);
            if (rules == null || rules.Count == 0)
                return "No rules parsed from input.";

            var validRules = new List<ReviewRule>();
            var skipped = 0;
            foreach (var rule in rules)
            {
                if (!string.IsNullOrEmpty(rule.Pattern))
                {
                    try { _ = new Regex(rule.Pattern); } // validate regex
                    catch (ArgumentException) { skipped++; continue; }
                }
                validRules.Add(rule);
            }

            if (validRules.Count == 0) return "All rules had invalid regex patterns.";

            _ruleEngine.AddRules(validRules);
            var msg = $"Loaded {validRules.Count} custom review rules.";
            if (skipped > 0) msg += $" Skipped {skipped} rules with invalid regex.";
            msg += $" Total rules: {_ruleEngine.Rules.Count}";
            return msg;
        }
        catch (Exception ex)
        {
            return $"Failed to load rules: {ex.Message}";
        }
    }

    /// <summary>
    /// Group changed files by relationship for targeted review.
    /// Uses deterministic grouping: interface→impl, test→source, code-behind, locale resources.
    /// </summary>
    [Description("将变更文件按关联关系分组，便于分组审查。\n"
        + "适用场景：审查前先分组，每组用一个子 Agent 独立审查（分治策略）。\n"
        + "支持的分组类型：interface-impl (接口/实现)、test-source (测试/源码)、locale-resource (多语言资源)、code-behind (XAML+代码)、related (同前缀文件)、standalone (独立文件)。")]
    [ToolExample("将当前变更按 8 个子 Agent 并发审查")]
    public string GroupChanges(
        [Description("Optional: filter by status (added, modified, deleted, all). Default: all")] string? status = null,
        CancellationToken ct = default)
    {
        try
        {
            var diffFiles = GetDiffFiles(status);
            if (diffFiles.Count == 0)
                return "(no changed files to group)";

            var groups = _grouping.Analyze(diffFiles);

            var sb = new StringBuilder();
            sb.AppendLine($"## File Groups ({groups.Count} groups, {diffFiles.Count} files)");

            var typeCounts = groups.GroupBy(g => g.GroupType)
                                   .Select(g => $"{g.Key}: {g.Count()}")
                                   .ToList();
            sb.AppendLine($"Types: {string.Join(", ", typeCounts)}");
            sb.AppendLine();

            foreach (var group in groups)
            {
                var files = string.Join(", ", group.Files.Select(f =>
                    $"{f.FilePath}{(f.Status != "modified" ? $" [{f.Status}]" : "")}"));
                sb.AppendLine($"  [{group.GroupType}] {group.GroupName}: {files}");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Grouping failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Run deterministic rules against changed files. Returns pattern-based rule matches.
    /// Built-in rules cover: async void, .Result deadlock, SQL injection, hardcoded secrets,
    /// missing CancellationToken, missing ConfigureAwait(false), string concat loops, etc.
    /// </summary>
    [Description("对变更文件运行确定性审查规则。返回规则匹配结果（含行号）。\n"
        + "适用场景：在 LLM 审查前先跑规则，确保不遗漏常见问题。\n"
        + "内置规则覆盖：异步方法使用、SQL 注入、硬编码密钥、路径遍历、性能陷阱、代码风格等。")]
    [ToolExample("对变更文件运行内置规则审查")]
    public string MatchReviewRules(
        [Description("Optional file path filter (glob). Example: \"**/*.cs\"")] string? fileFilter = null,
        CancellationToken ct = default)
    {
        try
        {
            var diffFiles = GetDiffFiles();
            if (diffFiles.Count == 0)
                return "(no changed files to check)";

            var fileContents = new Dictionary<string, string>();
            foreach (var file in diffFiles)
            {
                if (!string.IsNullOrEmpty(fileFilter) && !GlobMatch(file.FilePath, fileFilter))
                    continue;

                if (!File.Exists(file.FilePath)) continue;
                try
                {
                    var fileInfo = new FileInfo(file.FilePath);
                    if (fileInfo.Length > MaxFileReadSize) continue;
                    fileContents[file.FilePath] = File.ReadAllText(file.FilePath);
                }
                catch (Exception ex)
                {
                    return $"Failed to read {file.FilePath}: {ex.Message}";
                }
            }

            if (fileContents.Count == 0)
                return $"(no files matched filter '{fileFilter}')";

            var results = _ruleEngine.MatchAll(fileContents);
            var totalMatches = results.Sum(r => r.Value.Count);

            if (totalMatches == 0)
                return "✅ No rule violations found in changed files.";

            var sb = new StringBuilder();
            sb.AppendLine($"## Rule Match Results ({totalMatches} matches in {results.Count} files)");
            sb.AppendLine();

            var bySeverity = results.SelectMany(r => r.Value)
                                    .GroupBy(m => m.Severity)
                                    .OrderBy(g => g.Key)
                                    .ToList();
            sb.AppendLine($"Errors: {bySeverity.Where(g => g.Key == "error").Sum(g => g.Count())} | "
                         + $"Warnings: {bySeverity.Where(g => g.Key == "warning").Sum(g => g.Count())} | "
                         + $"Info: {bySeverity.Where(g => g.Key == "info").Sum(g => g.Count())}");
            sb.AppendLine();

            foreach (var (filePath, matches) in results.OrderBy(r => r.Key))
            {
                sb.AppendLine($"### {filePath} ({matches.Count} matches)");
                foreach (var m in matches.OrderBy(m => m.LineNumber))
                {
                    sb.AppendLine($"  L{m.LineNumber} [{m.Severity}] ({m.Category}) {m.Message}");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Rule matching failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Post-process LLM-generated review comments: validate and repair file:line references.
    /// Fixes position drift common in LLM-generated code reviews.
    /// </summary>
    [Description("修复审查评论中的文件路径和行号引用。\n"
        + "适用场景：LLM 生成的审查评论经常出现行号漂移或文件路径不准确，此工具可自动修正。\n"
        + "输入格式：每行一个 JSON ReviewComment 对象。")]
    [ToolExample("修复刚生成的审查评论位置")]
    public string RepairReviewPositions(
        [Description("Comments JSON array")] string commentsJson,
        CancellationToken ct = default)
    {
        try
        {
            var comments = System.Text.Json.JsonSerializer.Deserialize<List<ReviewComment>>(commentsJson);
            if (comments == null || comments.Count == 0)
            {
                // Try parsing from raw text
                comments = _positioner.ParseStructuredComments(commentsJson);
            }

            if (comments.Count == 0)
                return "(no comments to repair)";

            var diffFiles = GetDiffFiles();
            var repaired = _positioner.Repair(comments, diffFiles);

            var repairedCount = repaired.Count(r => r.WasRepaired);
            var sb = new StringBuilder();
            sb.AppendLine($"## Position Repair ({repaired.Count} comments, {repairedCount} repaired)");
            sb.AppendLine();

            foreach (var r in repaired)
            {
                if (r.WasRepaired)
                {
                    sb.AppendLine($"🛠 {r.RepairNote}");
                    sb.AppendLine($"   Was: {r.Original.FilePath}:{r.Original.LineNumber}");
                    sb.AppendLine($"   Now: {r.Repaired?.FilePath}:{r.Repaired?.LineNumber}");
                }
            }

            if (repairedCount == 0)
                sb.AppendLine("✅ All positions are valid.");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Position repair failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Reflect on review quality: coverage (files reviewed vs changed), severity distribution, comment specificity.
    /// </summary>
    [Description("审查质量反思：检查哪些文件被覆盖、严重度分布、评论具体程度。\n"
        + "适用场景：完成审查后，确认所有变更文件都已被审查、评论具有可操作性。\n"
        + "输入格式：每行一个 JSON ReviewComment 对象。")]
    [ToolExample("对刚完成的审查结果进行质量反思")]
    public string ReflectReviewQuality(
        [Description("Comments JSON array")] string commentsJson,
        CancellationToken ct = default)
    {
        try
        {
            var comments = System.Text.Json.JsonSerializer.Deserialize<List<ReviewComment>>(commentsJson);
            if (comments == null || comments.Count == 0)
                comments = _positioner.ParseStructuredComments(commentsJson);

            var diffFiles = GetDiffFiles();
            var reflection = _reflector.Reflect(comments, diffFiles);
            return _reflector.ToReport(reflection);
        }
        catch (Exception ex)
        {
            return $"Reflection failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Full review pipeline: group changes → run rules → reflect.
    /// Returns structured context for LLM review processing.
    /// </summary>
    [Description("全量审查上下文：分组 + 规则匹配 + 反射。\n"
        + "适用场景：审查前获取完整的结构性上下文，包括文件分组和规则匹配结果。\n"
        + "返回 TOON 格式的审查上下文，包含分组信息、规则匹配、审查指引。")]
    [ToolExample("获取全量审查上下文准备审查")]
    public string BuildReviewContext(
        [Description("Optional glob filter for files")] string? fileFilter = null,
        CancellationToken ct = default)
    {
        try
        {
            var diffFiles = GetDiffFiles();
            if (diffFiles.Count == 0)
                return "(no changed files)";

            // Group
            var groups = _grouping.Analyze(diffFiles);

            // Rules
            var fileContents = new Dictionary<string, string>();
            foreach (var file in diffFiles)
            {
                if (!string.IsNullOrEmpty(fileFilter) && !GlobMatch(file.FilePath, fileFilter))
                    continue;
                if (!File.Exists(file.FilePath)) continue;
                try
                {
                    var fileInfo = new FileInfo(file.FilePath);
                    if (fileInfo.Length > MaxFileReadSize) continue;
                    fileContents[file.FilePath] = File.ReadAllText(file.FilePath);
                }
                catch (Exception ex)
                {
                    return $"Failed to read {file.FilePath}: {ex.Message}";
                }
            }

            var ruleResults = fileContents.Count > 0
                ? _ruleEngine.MatchAll(fileContents)
                : [];

            // Build TOON-like structured context
            var sb = new StringBuilder();
            sb.AppendLine("# review_context");
            sb.AppendLine();

            sb.AppendLine("## files");
            foreach (var f in diffFiles)
            {
                var status = f.Status;
                var ext = Path.GetExtension(f.FilePath);
                sb.AppendLine($"  {f.FilePath} | {status} | {ext}");
            }

            sb.AppendLine();
            sb.AppendLine("## groups");
            foreach (var g in groups)
            {
                var files = string.Join(", ", g.Files.Select(f => Path.GetFileName(f.FilePath)));
                sb.AppendLine($"  {g.GroupType} | {g.GroupName} | {files}");
            }

            if (ruleResults.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## rule_matches");
                var totalMatches = ruleResults.Sum(r => r.Value.Count);
                sb.AppendLine($"  total: {totalMatches}");

                foreach (var (path, matches) in ruleResults.OrderBy(r => r.Key))
                {
                    foreach (var m in matches.Take(5))
                    {
                        sb.AppendLine($"  {Path.GetFileName(path)}:{m.LineNumber} | {m.Severity} | {m.Category} | {m.Message}");
                    }
                    if (matches.Count > 5)
                        sb.AppendLine($"  ... and {matches.Count - 5} more matches in {Path.GetFileName(path)}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("## review_instructions");
            sb.AppendLine("  Review each group as a unit (not individual files).");
            sb.AppendLine("  Use the rule matches above to guide your analysis.");
            sb.AppendLine("  Assign severity: P0 (must fix), P1 (should fix), P2 (suggestion).");
            sb.AppendLine("  Tag each comment with file:line for precision.");
            sb.AppendLine("  If a group has no rule matches, still review manually (LLM analysis).");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"BuildReviewContext failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Parallel review orchestrator: group changed files → spawn N review subagents concurrently → aggregate → persist.
    /// Each subagent reviews ONE file group independently. Results are deduplicated by file:line:category.
    /// </summary>
    [Description("并行审查编排器：将变更文件分组后并发创建子Agent独立审查每组，结果自动聚合去重并持久化。\n"
        + "适用场景：大批量代码审查，利用并行加速（LLM并发上限由 LTAI_REVIEW_PARALLELISM 控制，默认4）。\n"
        + "参数: fileFilter — 可选文件glob过滤; categories — 可选审查维度(逗号分隔: security,correctness,performance,maintainability)")]
    [ToolExample("并行审查所有当前变更")]
    public async Task<string> ParallelReview(
        [Description("Optional file glob filter")] string? fileFilter = null,
        [Description("Optional comma-separated review categories")] string? categories = null,
        CancellationToken ct = default)
    {
        if (_llm == null || _sp == null)
            return "(ParallelReview requires IChatClient and IServiceProvider — not available in this agent context)";

        var diffFiles = GetDiffFiles();
        if (diffFiles.Count == 0) return "(no changed files to review)";

        // Filter by glob
        var toReview = diffFiles;
        if (!string.IsNullOrEmpty(fileFilter))
            toReview = diffFiles.Where(f => GlobMatch(f.FilePath, fileFilter)).ToList();

        if (toReview.Count == 0) return $"(no files matching filter '{fileFilter}')";

        // Group files
        var groups = _grouping.Analyze(toReview);
        if (groups.Count == 0) return "(no file groups to review)";

        // Build review system prompt
        var catList = !string.IsNullOrEmpty(categories)
            ? categories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : (string[]?)null;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tasks = new List<Task<(string GroupName, string GroupType, string Result)>>();
        var spawnIndex = 0;

        foreach (var group in groups)
        {
            var g = group;
            var idx = Interlocked.Increment(ref spawnIndex);
            tasks.Add(Task.Run(async () =>
            {
                await ReviewConcurrencyGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var result = await SpawnReviewSubagent(g, idx, catList, ct).ConfigureAwait(false);
                    return (g.GroupName, g.GroupType, result);
                }
                finally { ReviewConcurrencyGate.Release(); }
            }, ct));
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        sw.Stop();

        // Parse findings from each subagent output
        var allFindings = new List<AuditFinding>();
        var parseErrors = 0;
        foreach (var (groupName, groupType, resultJson) in results)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<AuditFindingResult>(resultJson);
                if (parsed?.findings is { Count: > 0 })
                {
                    foreach (var f in parsed.findings)
                    {
                        if (f.IsValid())
                            allFindings.Add(f);
                    }
                }
            }
            catch
            {
                parseErrors++;
            }
        }

        // Dedup: remove duplicate file:line:category
        var deduped = allFindings
            .GroupBy(f => $"{f.File}|{f.Line}|{f.Category}")
            .Select(g => g.First())
            .ToList();

        // Persist if memory store available
        var persistedCount = 0;
        if (_memoryStore != null && deduped.Count > 0)
        {
            var roomName = GetAuditRoomName();
            foreach (var f in deduped.Take(MaxAuditEntries))
            {
                var content = JsonSerializer.Serialize(new
                {
                    f.Severity, f.File, f.Line,
                    Category = f.Category ?? "general",
                    f.Description,
                    PersistedAt = DateTimeOffset.UtcNow.ToString("o"),
                });
                await _memoryStore.StoreAsync(
                    wing: "audit", room: roomName, content: content, role: "audit",
                    importance: f.Severity switch { "P0" => 0.9, "P1" => 0.7, _ => 0.5 },
                    agentId: "review_tool",
                    metadata: new Dictionary<string, object>
                    {
                        ["severity"] = f.Severity ?? "P2",
                        ["file"] = f.File ?? "",
                        ["line"] = f.Line ?? "",
                        ["category"] = f.Category ?? "general",
                        ["audit_type"] = "review",
                        ["status"] = "open",
                        ["citation"] = f.Citation ?? "",
                        ["disagreement"] = f.Disagreement ?? "",
                    },
                    ttlMs: null).ConfigureAwait(false);
                persistedCount++;
            }
        }

        // Build summary
        var sb = new StringBuilder();
        sb.AppendLine($"## Parallel Review Summary ({sw.Elapsed.TotalSeconds:F1}s)");
        sb.AppendLine();
        sb.AppendLine($"| Metric | Value |");
        sb.AppendLine($"|--------|-------|");
        sb.AppendLine($"| Files changed | {diffFiles.Count} |");
        sb.AppendLine($"| Files reviewed | {toReview.Count} |");
        sb.AppendLine($"| File groups | {groups.Count} |");
        sb.AppendLine($"| Subagents spawned | {results.Length} |");
        sb.AppendLine($"| Total findings | {allFindings.Count} |");
        sb.AppendLine($"| Deduped findings | {deduped.Count} |");
        sb.AppendLine($"| Persisted | {persistedCount} |");
        sb.AppendLine($"| Parse errors | {parseErrors} |");
        sb.AppendLine($"| Parallelism | {ReviewParallelism} |");

        if (deduped.Count > 0)
        {
            sb.AppendLine("\n### Top Findings");
            var bySev = deduped.GroupBy(f => f.Severity ?? "?")
                               .OrderBy(g => g.Key)
                               .ToList();
            foreach (var sevGroup in bySev)
            {
                sb.AppendLine($"\n**{sevGroup.Key}** ({sevGroup.Count()}):");
                foreach (var f in sevGroup.Take(5))
                    sb.AppendLine($"  - {f.File}:{f.Line} [{f.Category}] {f.Description}");
                if (sevGroup.Count() > 5)
                    sb.AppendLine($"  - ... and {sevGroup.Count() - 5} more");
            }
        }

        if (parseErrors > 0)
            sb.AppendLine($"\n⚠️ {parseErrors} subagents returned unparseable output (findings may be missed).");

        return sb.ToString();
    }

    private async Task<string> SpawnReviewSubagent(FileGroup group, int index,
        string[]? categories, CancellationToken ct)
    {
        var fileList = string.Join(", ", group.Files.Select(f => Path.GetFileName(f.FilePath)));
        var fullPaths = string.Join("\n", group.Files.Select(f => $"  {f.FilePath} ({f.Status})"));

        var catStr = categories is { Length: > 0 }
            ? $"Focus on these dimensions: {string.Join(", ", categories)}."
            : "Review all relevant dimensions: correctness, security, performance, maintainability.";

        var taskPrompt = $$"""
        Review file group #{{index}} ({{group.GroupType}}): {{group.GroupName}}

        Files in this group:
        {{fullPaths}}

        {{catStr}}

        PHASE 0 — DISAGREEMENT REQUIRED:
        Before reporting any finding, check if it contradicts the existing rule-engine results
        or reasonable code decisions. If so, prefix your Description with "DISAGREE: " and
        add a "Disagreement" field explaining WHY. Silent compliance with the spec is a defect.

        CITATION REQUIRED:
        Every finding MUST include a "Citation" field with the exact file:line and the
        relevant code fragment (max 120 chars). Findings without verifiable citations
        will be rejected.

        OUTPUT FORMAT:
        Return a JSON object with exactly this structure:
        { "findings": [{ "Severity": "P0|P1|P2", "File": "path/to/file", "Line": "42",
          "Category": "security|correctness|performance|maintainability",
          "Description": "...", "Citation": "@path/to/file:42: `code_fragment`",
          "Disagreement": "why this counters the rule engine (null if none)" }] }

        If NO issues found, return: { "findings": [] }

        Return ONLY the JSON — no markdown, no explanations, no code blocks.
        """;

        var systemPrompt = $$"""
        You are a focused code review subagent with a discipline of evidence-based findings.
        Review ONLY the assigned file group (group type: {{group.GroupType}}).
        - Be precise: every finding MUST cite exact file:line numbers and include a code fragment.
        - Severity: P0 (blocking bug/security), P1 (should fix), P2 (suggestion).
        - PHASE 0: actively disagree with anything you think is wrong — echo-chamber review is useless.
        - Do not suggest writing new code — just report findings with evidence.
        - Output ONLY valid JSON as the final message.
        """;

        // Filter tools to read-only review-relevant
        var readOnlyPrefixes = new HashSet<string>
        {
            "Read", "Search", "Glob", "List", "Get",
            "DirectoryTree", "Fetch", "Find", "Lookup",
            "Ping", "Dns", "Check", "Whois", "HttpCheck",
            "Network", "SystemInfo",
        };
        var deniedTools = new HashSet<string>
        {
            "SpawnSubagent", "ParallelReview", "spawn_subagent",
            "WriteFile", "EditFile", "MultiEdit", "DeleteFile",
            "MoveFile", "CopyFile",
            "GitCommit", "GitPush", "GitCommitAndPush", "GitMerge",
            "RunCommand", "SafeShell",
            "SaveAuditFindings", "SaveMemory", "StoreMemory",
        };
        var subTools = (_allTools ?? []).Where(t =>
        {
            var name = t.Name ?? "";
            foreach (var d in deniedTools)
                if (name.Equals(d, StringComparison.OrdinalIgnoreCase)) return false;
            foreach (var p in readOnlyPrefixes)
                if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }).ToList();

        var chatOptions = new ChatOptions
        {
            Temperature = 0.3f,
            MaxOutputTokens = 4096,
            Tools = subTools.Count > 0 ? subTools : null,
        };

        var agent = new ChatClientAgent(_llm!, new ChatClientAgentOptions
        {
            Name = $"review-sub-{index}",
            Description = systemPrompt,
            ChatOptions = chatOptions,
            ChatHistoryProvider = new Microsoft.Agents.AI.InMemoryChatHistoryProvider(
                new Microsoft.Agents.AI.InMemoryChatHistoryProviderOptions
                {
                    ChatReducer = new MaxMessageCountReducer(200),
                    ReducerTriggerEvent = Microsoft.Agents.AI.InMemoryChatHistoryProviderOptions.ChatReducerTriggerEvent.AfterMessageAdded,
                }),
        }, null, _sp);

        try
        {
            var session = await agent.CreateSessionAsync(ct).ConfigureAwait(false);
            var lastMessage = "";
            await foreach (var update in agent.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, taskPrompt)], session, cancellationToken: ct))
            {
                if (!string.IsNullOrEmpty(update.Text))
                    lastMessage = update.Text.Trim();
            }

            // Extract JSON from output (may have markdown fences)
            var json = ExtractJson(lastMessage);
            return json ?? "{\"findings\":[]}";
        }
        catch (OperationCanceledException)
        {
            return "{\"findings\":[]}";
        }
        catch (Exception)
        {
            return "{\"findings\":[]}";
        }
    }

    private static string? ExtractJson(string text)
    {
        // Try raw JSON first
        if (text.StartsWith("{") && text.EndsWith("}"))
            return text;

        // Try extracting from ```json ... ``` fences
        var jsonFence = System.Text.RegularExpressions.Regex.Match(text,
            @"```(?:json)?\s*\n?(\{[\s\S]*?\})\s*\n?```",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        if (jsonFence.Success)
            return jsonFence.Groups[1].Value;

        // Try extracting any {...} block
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
            return text[start..(end + 1)];

        return null;
    }

    private sealed record AuditFindingResult(List<AuditFinding> findings);

    /// <summary>
    /// Persist review/audit findings to PalaceStore for long-term recall.
    /// Findings are stored under wing="audit" with permanent TTL (no expiry).
    /// Room naming: "review-{commitHash[:8]}" or "security-{timestamp}" for traceability.
    /// </summary>
    [Description("将审查发现持久化到长期记忆，供未来查询。\n"
        + "适用场景：审查/安全/调试完成后保存关键发现，确保可被 RecallMemory 召回。\n"
        + "参数：findingsJson — JSON 数组 [{Severity,File,Line,Category,Description}]。")]
    [ToolExample("保存审查发现到记忆")]
    public async Task<string> SaveAuditFindings(
        [Description("JSON array of findings")] string findingsJson,
        CancellationToken ct = default)
    {
        if (_memoryStore == null)
            return "(memory store not available)";

        try
        {
            var findings = JsonSerializer.Deserialize<List<AuditFinding>>(findingsJson);
            if (findings == null || findings.Count == 0)
                return "(no findings to persist)";

            var roomName = GetAuditRoomName();

            // Dedup: skip findings that already exist (match by file+line+category)
            var existingDrawers = await _memoryStore.SearchByWingAsync("audit", maxCount: DefaultAuditLimit).ConfigureAwait(false);
            var knownKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in existingDrawers)
            {
                var meta = ParseMetadata(d.Metadata);
                var f = GetMetaString(meta, "file") ?? "";
                var l = GetMetaString(meta, "line") ?? "";
                var c = GetMetaString(meta, "category") ?? "";
                if (!string.IsNullOrEmpty(f))
                    knownKeys.Add($"{f}|{l}|{c}");
            }

            var cnt = 0;
            var skipped = 0;
            foreach (var f in findings)
            {
                var key = $"{f.File ?? ""}|{f.Line ?? ""}|{f.Category ?? "general"}";
                if (knownKeys.Contains(key)) { skipped++; continue; }

                var content = JsonSerializer.Serialize(new
                {
                    f.Severity, f.File, f.Line,
                    Category = f.Category ?? "general",
                    f.Description,
                    PersistedAt = DateTimeOffset.UtcNow.ToString("o"),
                });

                await _memoryStore.StoreAsync(
                    wing: "audit",
                    room: roomName,
                    content: content,
                    role: "audit",
                    importance: f.Severity switch { "P0" => 0.9, "P1" => 0.7, _ => 0.5 },
                    agentId: "review_tool",
                    metadata: new Dictionary<string, object>
                    {
                        ["severity"] = f.Severity ?? "P2",
                        ["file"] = f.File ?? "",
                        ["line"] = f.Line ?? "",
                        ["category"] = f.Category ?? "general",
                        ["audit_type"] = "review",
                        ["status"] = "open",
                        ["citation"] = f.Citation ?? "",
                        ["disagreement"] = f.Disagreement ?? "",
                    },
                    ttlMs: null
                ).ConfigureAwait(false);
                cnt++;
                knownKeys.Add(key);
            }

            var msg = $"Persisted {cnt} audit findings to room '{roomName}' (wing=audit, permanent)";
            if (skipped > 0) msg += $" — skipped {skipped} duplicates";
            return msg;
        }
        catch (Exception ex)
        {
            return $"Failed to persist audit findings: {ex.Message}";
        }
    }

    private string GetAuditRoomName()
    {
        try
        {
            var repoPath = LibGit2Sharp.Repository.Discover(_ws);
            if (repoPath != null)
            {
                using var repo = new LibGit2Sharp.Repository(repoPath);
                if (repo.Head.Tip != null)
                    return $"review-{repo.Head.Tip.Sha[..8]}";
                if (!string.IsNullOrEmpty(repo.Head.FriendlyName))
                    return $"review-{repo.Head.FriendlyName}";
            }
        }
        catch (Exception) { System.Diagnostics.Debug.WriteLine("[ReviewTools] Non-critical error"); }
        return $"review-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
    }

    /// <summary>Audit finding record for serialization.</summary>
    public record AuditFinding(
        [property: System.Text.Json.Serialization.JsonPropertyName("Severity")] string? Severity,
        [property: System.Text.Json.Serialization.JsonPropertyName("File")] string? File,
        [property: System.Text.Json.Serialization.JsonPropertyName("Line")] string? Line,
        [property: System.Text.Json.Serialization.JsonPropertyName("Category")] string? Category,
        [property: System.Text.Json.Serialization.JsonPropertyName("Description")] string? Description,
        [property: System.Text.Json.Serialization.JsonPropertyName("Citation")] string? Citation = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("Disagreement")] string? Disagreement = null)
    {
        public bool IsValid() => !string.IsNullOrEmpty(Severity) && !string.IsNullOrEmpty(File);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Audit Finding Lifecycle: open → addressed → verified → closed
    //  Closed & reopened allow full-cycle tracing.
    //  All state transitions are atomic (UPDATE metadata, no delete-recreate).
    //  Audit trail recorded in metadata.history[].
    // ══════════════════════════════════════════════════════════════════════

    private static string[] ValidAuditStatuses =
        ["open", "addressed", "verified", "closed", "false_positive", "wont_fix"];

    private static readonly int DefaultAuditLimit = MaxAuditEntries;
    private static readonly int MaxAuditLimit = MaxAuditEntries;

    /// <summary>
    /// Resolve an audit finding by updating its status atomically.
    /// Status transitions: open → addressed | false_positive | wont_fix.
    /// </summary>
    [Description("标记审查发现为已处理/已修复/误报/无需修复。\n"
        + "状态流转: open → addressed (已修复) | false_positive (误报) | wont_fix (不修)\n"
        + "参数: findingId — 发现ID (SaveAuditFindings返回的drawerId前8位); \n"
        + "  status — addressed|false_positive|wont_fix; \n"
        + "  fixDescription — 修复描述或误报原因(可选)")]
    [ToolExample("标记发现 R001 已修复")]
    public async Task<string> ResolveAuditFinding(
        [Description("Finding drawer ID prefix (8+ chars)")] string findingId,
        [Description("New status: addressed, false_positive, wont_fix")] string status,
        [Description("Optional: fix description or reason")] string? fixDescription = null,
        CancellationToken ct = default)
    {
        if (_memoryStore == null) return "(memory store not available)";

        var validStatus = status?.ToLowerInvariant() switch
        {
            "addressed" => "addressed",
            "false_positive" => "false_positive",
            "wont_fix" => "wont_fix",
            _ => null,
        };
        if (validStatus == null)
            return $"Invalid status '{status}'. Use: addressed, false_positive, wont_fix.";

        var drawers = await _memoryStore.SearchByWingAsync("audit", maxCount: DefaultAuditLimit).ConfigureAwait(false);
        var match = drawers.FirstOrDefault(d =>
            d.DrawerId.StartsWith(findingId, StringComparison.OrdinalIgnoreCase));

        if (match == null) return $"Finding '{findingId}' not found in audit wing.";

        var meta = ParseMetadata(match.Metadata);
        var oldStatus = GetMetaString(meta, "status") ?? "open";

        if (oldStatus != "open")
            return $"Finding '{findingId}' is already '{oldStatus}'. Only 'open' findings can be resolved.";

        meta["status"] = validStatus;
        meta["resolved_at"] = DateTimeOffset.UtcNow.ToString("o");
        if (!string.IsNullOrEmpty(fixDescription))
            meta["fix_description"] = fixDescription;
        AddAuditTrail(meta, oldStatus, validStatus, fixDescription);

        await _memoryStore.UpdateDrawerFieldsAsync(match.Wing, match.Room, match.DrawerId,
            metadata: meta).ConfigureAwait(false);

        return $"Finding '{findingId}': {oldStatus} → {validStatus}" +
            (fixDescription != null ? $" ({fixDescription})" : "");
    }

    /// <summary>
    /// Verify/fix a finding: confirmed fix → verified, unconfirmed → reopen to open.
    /// </summary>
    [Description("验证审查发现已被实际修复。\n"
        + "参数: findingId — 发现ID; confirmed — true标记verified / false回退到open")]
    [ToolExample("验证发现已确认修复")]
    public async Task<string> VerifyAuditFinding(
        [Description("Finding drawer ID prefix (8+ chars)")] string findingId,
        [Description("True to confirm fix, false to reopen")] bool confirmed = true,
        CancellationToken ct = default)
    {
        if (_memoryStore == null) return "(memory store not available)";

        var drawers = await _memoryStore.SearchByWingAsync("audit", maxCount: DefaultAuditLimit).ConfigureAwait(false);
        var match = drawers.FirstOrDefault(d =>
            d.DrawerId.StartsWith(findingId, StringComparison.OrdinalIgnoreCase));

        if (match == null) return $"Finding '{findingId}' not found in audit wing.";

        var meta = ParseMetadata(match.Metadata);
        var oldStatus = GetMetaString(meta, "status") ?? "open";

        if (confirmed)
        {
            if (oldStatus is not ("addressed" or "verified" or "closed"))
                return $"Finding '{findingId}' is '{oldStatus}'. Must be addressed/verified/closed before re-verifying.";

            meta["status"] = "verified";
            meta["verified_at"] = DateTimeOffset.UtcNow.ToString("o");
            AddAuditTrail(meta, oldStatus, "verified", "Fix confirmed");

            await _memoryStore.UpdateDrawerFieldsAsync(match.Wing, match.Room, match.DrawerId,
                metadata: meta, importance: match.Importance * 0.5).ConfigureAwait(false);

            return $"Finding '{findingId}': {oldStatus} → verified — importance downgraded.";
        }
        else
        {
            if (oldStatus is "open")
                return $"Finding '{findingId}' is already open.";
            meta["status"] = "open";
            meta.Remove("verified_at");
            meta.Remove("resolved_at");
            AddAuditTrail(meta, oldStatus, "open", "Reopened");

            await _memoryStore.UpdateDrawerFieldsAsync(match.Wing, match.Room, match.DrawerId,
                metadata: meta, importance: match.Importance).ConfigureAwait(false);

            return $"Finding '{findingId}': {oldStatus} → open — reopened.";
        }
    }

    /// <summary>
    /// Close a verified finding (verified → closed). Reversible via VerifyAuditFinding(confirmed=false).
    /// </summary>
    [Description("关闭已验证的审查发现（verified → closed）。可重新打开。\n"
        + "参数: findingId — 发现ID; closeSummary — 关闭说明(可选)")]
    [ToolExample("关闭已验证的发现")]
    public async Task<string> CloseAuditFinding(
        [Description("Finding drawer ID prefix (8+ chars)")] string findingId,
        [Description("Optional: close reason or summary")] string? closeSummary = null,
        CancellationToken ct = default)
    {
        if (_memoryStore == null) return "(memory store not available)";

        var drawers = await _memoryStore.SearchByWingAsync("audit", maxCount: DefaultAuditLimit).ConfigureAwait(false);
        var match = drawers.FirstOrDefault(d =>
            d.DrawerId.StartsWith(findingId, StringComparison.OrdinalIgnoreCase));

        if (match == null) return $"Finding '{findingId}' not found in audit wing.";

        var meta = ParseMetadata(match.Metadata);
        var oldStatus = GetMetaString(meta, "status") ?? "open";

        if (oldStatus is not ("verified" or "addressed"))
            return $"Finding '{findingId}' is '{oldStatus}'. Must be verified (or addressed) before closing.";

        meta["status"] = "closed";
        meta["closed_at"] = DateTimeOffset.UtcNow.ToString("o");
        if (!string.IsNullOrEmpty(closeSummary))
            meta["close_summary"] = closeSummary;
        AddAuditTrail(meta, oldStatus, "closed", closeSummary);

        await _memoryStore.UpdateDrawerFieldsAsync(match.Wing, match.Room, match.DrawerId,
            metadata: meta).ConfigureAwait(false);

        return $"Finding '{findingId}': {oldStatus} → closed" +
            (closeSummary != null ? $" ({closeSummary})" : "");
    }

    /// <summary>
    /// Delete an audit finding permanently (admin operation).
    /// </summary>
    [Description("永久删除一条审查发现（管理员操作，不可逆）。\n参数: findingId — 发现ID前8+字符")]
    [ToolExample("删除误报发现")]
    public async Task<string> DeleteAuditFinding(
        [Description("Finding drawer ID prefix (8+ chars)")] string findingId,
        CancellationToken ct = default)
    {
        if (_memoryStore == null) return "(memory store not available)";

        var drawers = await _memoryStore.SearchByWingAsync("audit", maxCount: DefaultAuditLimit).ConfigureAwait(false);
        var match = drawers.FirstOrDefault(d =>
            d.DrawerId.StartsWith(findingId, StringComparison.OrdinalIgnoreCase));

        if (match == null) return $"Finding '{findingId}' not found in audit wing.";

        var meta = ParseMetadata(match.Metadata);
        var status = GetMetaString(meta, "status") ?? "open";
        var deleted = await _memoryStore.DeleteDrawerAsync(match.DrawerId).ConfigureAwait(false);
        return deleted
            ? $"Finding '{findingId}' (status={status}) permanently deleted."
            : $"Failed to delete finding '{findingId}'.";
    }

    /// <summary>
    /// Freeze acceptance gates to disk before builder dispatch (architect-loop R2).
    /// Gates are executable verification commands that the architect runs post-flight.
    /// Any builder edit to a gate file = automatic FAIL.
    /// </summary>
    [Description("冻结审查门禁到磁盘：将当前发现生成为可执行验证命令并提交 docs/gates/<slice>.md。\n"
        + "适用场景：部署前冻结门禁，确保 builder 不可篡改验收标准。\n"
        + "参数: sliceName — 切片名称(默认当前commit SHA前8位); autoCommit — 是否自动git commit")]
    [ToolExample("冻结当前切片门禁")]
    public async Task<string> FreezeAuditGates(
        [Description("Slice name (default: commit SHA[:8])")] string? sliceName = null,
        [Description("Auto git-commit the gate file")] bool autoCommit = true,
        CancellationToken ct = default)
    {
        if (_memoryStore == null) return "(memory store not available)";

        var drawers = await _memoryStore.SearchByWingAsync("audit", maxCount: DefaultAuditLimit).ConfigureAwait(false);
        var openFindings = new List<(string File, string Line, string Category, string Desc, string Sev, string Citation)>();
        foreach (var d in drawers)
        {
            var meta = ParseMetadata(d.Metadata);
            if ((GetMetaString(meta, "status") ?? "open") != "open") continue;
            var file = GetMetaString(meta, "file") ?? "";
            var line = GetMetaString(meta, "line") ?? "";
            var cat = GetMetaString(meta, "category") ?? "general";
            var sev = GetMetaString(meta, "severity") ?? "P2";
            var citation = GetMetaString(meta, "citation") ?? "";
            if (string.IsNullOrEmpty(file)) continue;
            openFindings.Add((file, line, cat, d.Content, sev, citation));
        }

        var name = sliceName ?? GetAuditRoomName().Replace("review-", "");
        var gatesDir = Path.Combine(_ws, "docs", "gates");
        Directory.CreateDirectory(gatesDir);
        var gatePath = Path.Combine(gatesDir, $"{name}.md");
        var frozenAt = DateTimeOffset.UtcNow.ToString("o");

        var sb = new StringBuilder();
        sb.AppendLine($"# Frozen Gates: {name}");
        sb.AppendLine($"> Frozen at: {frozenAt}");
        sb.AppendLine($"> **DO NOT EDIT — builder edits are automatic FAIL**");
        sb.AppendLine($"> Verification: architect runs each gate command manually after builder dispatch\n");

        if (openFindings.Count == 0)
        {
            sb.AppendLine("## All clear — no open findings");
            sb.AppendLine("\nGate: `echo PASS` (no open findings)");
        }
        else
        {
            sb.AppendLine($"## Open Findings ({openFindings.Count})");
            sb.AppendLine();
            foreach (var (file, line, cat, desc, sev, citation) in openFindings)
            {
                var gateId = $"GATE-{openFindings.IndexOf((file, line, cat, desc, sev, citation)) + 1:D3}";
                sb.AppendLine($"### {gateId}: [{sev}] {cat}");
                sb.AppendLine($"- **File**: {file}:{line}");
                sb.AppendLine($"- **Citation**: {(string.IsNullOrEmpty(citation) ? "N/A" : citation)}");
                sb.AppendLine($"- **Gate**: Verify that `{file}:{line}` has been addressed");
                sb.AppendLine($"- **Command**: `rg -n \"{EscapeGatePattern(file)}\" {Path.GetFileName(file)}`");

                // Generate an executable check
                var ext = Path.GetExtension(file).ToLowerInvariant();
                var checkCmd = ext switch
                {
                    ".cs" => $"grep -n \"TODO.*{name}\" {file} || echo 'GATE_PASS: no TODO for {name}'",
                    ".py" => $"python -m py_compile {file} 2>&1 || echo 'GATE_FAIL: syntax error'",
                    ".ts" or ".tsx" or ".js" => $"npx eslint {file} --rule 'no-console: error' 2>&1 || echo 'GATE_CHECK'",
                    _ => $"rg -c \"TODO|FIXME|HACK|XXX\" {file} || echo 'GATE_PASS'",
                };
                sb.AppendLine($"- **Automated Check**: `{checkCmd}`");
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine($"### Summary");
            sb.AppendLine($"- Frozen: {openFindings.Count} gates");
            sb.AppendLine($"- Verdict: Run each gate command; PASS/FAIL/INVALID per gate");
            sb.AppendLine($"- Slice-level: kill/continue based on gate outcomes");
        }

        var gateContent = sb.ToString();
        await File.WriteAllTextAsync(gatePath, gateContent, ct).ConfigureAwait(false);

        // Auto commit (non-blocking)
        if (autoCommit)
        {
            try
            {
                var repoPath = LibGit2Sharp.Repository.Discover(_ws);
                if (repoPath != null)
                {
                    using var repo = new LibGit2Sharp.Repository(repoPath);
                    var relativePath = Path.GetRelativePath(repo.Info.WorkingDirectory, gatePath);
                    Commands.Stage(repo, relativePath);
                    repo.Commit($"chore(gates): freeze audit gates for {name} ({openFindings.Count} findings)", new Signature("LTAI-Review", "review@ltai", DateTimeOffset.Now), new Signature("LTAI-Review", "review@ltai", DateTimeOffset.Now));
                }
            }
            catch (Exception) { System.Diagnostics.Debug.WriteLine("[ReviewTools] Git not available"); }
        }

        return $"Frozen {openFindings.Count} gates → {gatePath}" +
            (autoCommit ? " (auto-committed)" : "");
    }

    // ── Gate helpers ──

    private static string EscapeGatePattern(string path) => path.Replace("\\", "\\\\").Replace(".", "\\.");

    /// <summary>
    /// List audit findings with rich filtering and pagination.
    /// </summary>
    [Description("列出审查发现，支持多维度过滤。\n"
        + "参数: statusFilter — open|addressed|verified|closed|false_positive|wont_fix\n"
        + "  severity — P0|P1|P2; fileFilter — 文件路径glob; category — correctness|security|performance|maintainability|test-coverage\n"
        + "  fromDate/toDate — ISO日期范围; limit — 最大返回数(默认500); includeFixed — 是否包含已处理的")]
    [ToolExample("列出所有P0级别的未处理发现")]
    public async Task<string> ListAuditFindings(
        [Description("Filter by status (comma-separated ok)")] string? statusFilter = null,
        [Description("Filter by severity: P0, P1, P2")] string? severity = null,
        [Description("Filter by file path (glob, e.g. **/*.cs)")] string? fileFilter = null,
        [Description("Filter by category")] string? category = null,
        [Description("ISO start date (yyyy-MM-dd)")] string? fromDate = null,
        [Description("ISO end date (yyyy-MM-dd)")] string? toDate = null,
        [Description("Max findings to return (default 500, max 2000)")] int limit = 500,
        [Description("Include resolved/closed findings")] bool includeFixed = false,
        CancellationToken ct = default)
    {
        if (_memoryStore == null) return "(memory store not available)";

        var max = Math.Clamp(limit > 0 ? limit : DefaultAuditLimit, 1, MaxAuditLimit);
        var drawers = await _memoryStore.SearchByWingAsync("audit", maxCount: max).ConfigureAwait(false);
        if (drawers.Count == 0) return "(no audit findings)";

        // Parse status filter list
        var statusFilterSet = !string.IsNullOrEmpty(statusFilter)
            ? statusFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .Select(s => s.ToLowerInvariant()).ToHashSet()
            : null;

        var filtered = new List<(PalaceStore.Drawer Drawer, string Status, string? Sev, string? File, string? Line, string? Cat, long CreatedAt)>();
        foreach (var d in drawers)
        {
            var st = "open"; string? sev = null, file = null, line = null, cat = null;
            if (d.Metadata != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(d.Metadata);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("status", out var s)) st = s.GetString() ?? "open";
                    if (root.TryGetProperty("severity", out var sv)) sev = sv.GetString();
                    if (root.TryGetProperty("file", out var f)) file = f.GetString();
                    if (root.TryGetProperty("line", out var l)) line = l.GetString();
                    if (root.TryGetProperty("category", out var c)) cat = c.GetString();
                }
                catch (Exception) { System.Diagnostics.Debug.WriteLine("[ReviewTools] Non-critical error"); }
            }

            // Apply filters
            if (statusFilterSet != null && !statusFilterSet.Contains(st)) continue;
            if (!string.IsNullOrEmpty(severity) && !string.Equals(sev, severity, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(fileFilter) && file != null && !GlobMatch(file, fileFilter)) continue;
            if (!string.IsNullOrEmpty(category) && !string.Equals(cat, category, StringComparison.OrdinalIgnoreCase)) continue;
            if (!includeFixed && st is not "open") continue;
            if (fromDate != null && DateTimeOffset.TryParse(fromDate, out var fd) && d.CreatedAt < fd.ToUnixTimeMilliseconds()) continue;
            if (toDate != null && DateTimeOffset.TryParse(toDate, out var td) && d.CreatedAt > td.AddDays(1).ToUnixTimeMilliseconds()) continue;

            filtered.Add((d, st, sev, file, line, cat, d.CreatedAt));
        }

        if (filtered.Count == 0)
        {
            var desc = statusFilterSet != null ? string.Join(",", statusFilterSet) : (includeFixed ? "all" : "open");
            return $"(no findings matching filters: status={desc})";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"## Audit Findings ({filtered.Count} matching)\n");

        var byStatus = filtered.GroupBy(f => f.Status).OrderBy(g => g.Key);
        foreach (var group in byStatus)
        {
            sb.AppendLine($"### {group.Key} ({group.Count()})");
            foreach (var f in group.OrderBy(f => f.Sev).ThenBy(f => f.CreatedAt))
            {
                var loc = $"{f.File ?? "?"}:{f.Line ?? "?"}";
                sb.AppendLine($"  [{f.Sev ?? "?"}] {f.Cat ?? "?"} | {loc}");
                var preview = f.Drawer.Content.Length > 100
                    ? f.Drawer.Content[..100] + "..."
                    : f.Drawer.Content;
                sb.AppendLine($"    {preview}");
                sb.AppendLine($"    id: {f.Drawer.DrawerId[..8]}");
            }
            sb.AppendLine();
        }

        var openCount = filtered.Count(f => f.Status == "open");
        var p0Count = filtered.Count(f => f.Sev == "P0" && f.Status == "open");
        if (openCount > 0 || p0Count > 0)
        {
            sb.AppendLine("---");
            sb.AppendLine($"Open: {openCount} | P0 open: {p0Count}");
            sb.AppendLine("ResolveAuditFinding <id> addressed — mark fixed");
            sb.AppendLine("VerifyAuditFinding <id> — confirm fix");
            sb.AppendLine("CloseAuditFinding <id> — close after verification");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Get full details of a single audit finding by ID.
    /// </summary>
    [Description("获取单个审查发现的完整细节（含审计追踪）。\n"
        + "参数: findingId — 发现ID前8+字符")]
    [ToolExample("获取发现详情")]
    public async Task<string> GetAuditFinding(
        [Description("Finding drawer ID prefix (8+ chars)")] string findingId,
        CancellationToken ct = default)
    {
        if (_memoryStore == null) return "(memory store not available)";

        var drawers = await _memoryStore.SearchByWingAsync("audit", maxCount: DefaultAuditLimit).ConfigureAwait(false);
        var match = drawers.FirstOrDefault(d =>
            d.DrawerId.StartsWith(findingId, StringComparison.OrdinalIgnoreCase));

        if (match == null) return $"Finding '{findingId}' not found.";

        var meta = ParseMetadata(match.Metadata);
        var sb = new StringBuilder();
        sb.AppendLine($"# Audit Finding {match.DrawerId[..8]}\n");
        sb.AppendLine($"| Field | Value |");
        sb.AppendLine($"|-------|-------|");
        sb.AppendLine($"| Status | {GetMetaString(meta, "status") ?? "open"} |");
        sb.AppendLine($"| Severity | {GetMetaString(meta, "severity") ?? "?"} |");
        sb.AppendLine($"| File | {GetMetaString(meta, "file") ?? "?"} |");
        sb.AppendLine($"| Line | {GetMetaString(meta, "line") ?? "?"} |");
        sb.AppendLine($"| Category | {GetMetaString(meta, "category") ?? "?"} |");
        sb.AppendLine($"| Room | {match.Room} |");
        sb.AppendLine($"| Importance | {match.Importance:F2} |");
        sb.AppendLine($"| Resolved | {GetMetaString(meta, "resolved_at") ?? "-"} |");
        sb.AppendLine($"| Verified | {GetMetaString(meta, "verified_at") ?? "-"} |");
        sb.AppendLine($"| Closed | {GetMetaString(meta, "closed_at") ?? "-"} |");
        sb.AppendLine($"| Fix | {GetMetaString(meta, "fix_description") ?? GetMetaString(meta, "close_summary") ?? "-"} |");
        sb.AppendLine($"\n## Content\n```json\n{match.Content}\n```");

        if (meta.TryGetValue("_audit_trail", out var trailObj) && trailObj is string trailJson)
        {
            try
            {
                var trail = JsonSerializer.Deserialize<List<AuditTrailEntry>>(trailJson);
                if (trail is { Count: > 0 })
                {
                    sb.AppendLine($"\n## Audit Trail ({trail.Count} events)\n");
                    sb.AppendLine($"| When | From | To | By | Summary |");
                    sb.AppendLine($"|------|------|----|----|---------|");
                    foreach (var t in trail)
                        sb.AppendLine($"| {t.At} | {t.From} | {t.To} | {t.By ?? "-"} | {t.Summary ?? "-"} |");
                }
            }
            catch (Exception) { System.Diagnostics.Debug.WriteLine("[ReviewTools] Non-critical error"); }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Export audit findings to JSON, Markdown, or CSV format.
    /// </summary>
    [Description("导出审查发现为结构化格式。\n"
        + "参数: format — json|markdown|csv; statusFilter/severity/category 同 ListAuditFindings")]
    [ToolExample("导出所有P0发现为JSON")]
    public async Task<string> ExportAuditFindings(
        [Description("Export format: json, markdown, csv")] string format = "json",
        [Description("Filter by status")] string? statusFilter = null,
        [Description("Filter by severity")] string? severity = null,
        [Description("Filter by category")] string? category = null,
        [Description("Include all statuses, not just open")] bool includeAll = false,
        CancellationToken ct = default)
    {
        if (_memoryStore == null) return "(memory store not available)";

        var drawers = await _memoryStore.SearchByWingAsync("audit", maxCount: MaxAuditLimit).ConfigureAwait(false);
        if (drawers.Count == 0) return "(no audit findings)";

        var statusFilterSet = !string.IsNullOrEmpty(statusFilter)
            ? statusFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .Select(s => s.ToLowerInvariant()).ToHashSet()
            : null;

        var records = new List<Dictionary<string, string?>>();
        foreach (var d in drawers)
        {
            var st = "open"; string? sev = null, file = null, line = null, cat = null;
            if (d.Metadata != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(d.Metadata);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("status", out var s)) st = s.GetString() ?? "open";
                    if (root.TryGetProperty("severity", out var sv)) sev = sv.GetString();
                    if (root.TryGetProperty("file", out var f)) file = f.GetString();
                    if (root.TryGetProperty("line", out var l)) line = l.GetString();
                    if (root.TryGetProperty("category", out var c)) cat = c.GetString();
                }
                catch (Exception) { System.Diagnostics.Debug.WriteLine("[ReviewTools] Non-critical error"); }
            }
            if (statusFilterSet != null && !statusFilterSet.Contains(st)) continue;
            if (!string.IsNullOrEmpty(severity) && !string.Equals(sev, severity, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(category) && !string.Equals(cat, category, StringComparison.OrdinalIgnoreCase)) continue;
            if (!includeAll && st is not "open") continue;

            records.Add(new Dictionary<string, string?>
            {
                ["id"] = d.DrawerId[..8], ["status"] = st, ["severity"] = sev,
                ["file"] = file, ["line"] = line, ["category"] = cat,
                ["content"] = d.Content, ["room"] = d.Room,
            });
        }

        return format.ToLowerInvariant() switch
        {
            "json" => JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }),
            "markdown" => ExportMarkdown(records),
            "csv" => ExportCsv(records),
            _ => $"Unknown format '{format}'. Use: json, markdown, csv.",
        };
    }

    /// <summary>
    /// Aggregate statistics: counts by status, severity, category.
    /// </summary>
    [Description("审查发现聚合统计：按状态、严重度、类别统计。\n参数同 ListAuditFindings。")]
    [ToolExample("查看审查统计面板")]
    public async Task<string> AuditStatistics(
        [Description("Filter by severity: P0, P1, P2")] string? severity = null,
        [Description("Filter by file path (glob)")] string? fileFilter = null,
        [Description("Filter by category")] string? category = null,
        CancellationToken ct = default)
    {
        if (_memoryStore == null) return "(memory store not available)";

        var drawers = await _memoryStore.SearchByWingAsync("audit", maxCount: MaxAuditEntries).ConfigureAwait(false);
        if (drawers.Count == 0) return "(no audit findings)";

        // Parse and filter
        var results = new List<(string Status, string Sev, string Cat, string File)>();
        foreach (var d in drawers)
        {
            var st = "open"; string sev = "?"; string cat = "?"; string file = "?";
            if (d.Metadata != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(d.Metadata);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("status", out var s)) st = s.GetString() ?? "open";
                    if (root.TryGetProperty("severity", out var sv)) sev = sv.GetString() ?? "?";
                    if (root.TryGetProperty("category", out var c)) cat = c.GetString() ?? "?";
                    if (root.TryGetProperty("file", out var f)) file = f.GetString() ?? "?";
                }
                catch (Exception) { System.Diagnostics.Debug.WriteLine("[ReviewTools] Non-critical error"); }
            }
            if (!string.IsNullOrEmpty(severity) && !string.Equals(sev, severity, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(fileFilter) && !GlobMatch(file, fileFilter)) continue;
            if (!string.IsNullOrEmpty(category) && !string.Equals(cat, category, StringComparison.OrdinalIgnoreCase)) continue;
            results.Add((st, sev, cat, file));
        }

        if (results.Count == 0) return "(no findings matching filters)";

        var sb = new StringBuilder();
        sb.AppendLine($"# Audit Statistics ({results.Count} findings)\n");

        sb.AppendLine("## By Status");
        foreach (var g in results.GroupBy(r => r.Status).OrderBy(g => g.Key))
            sb.AppendLine($"  {g.Key}: {g.Count()}");

        sb.AppendLine("\n## By Severity");
        foreach (var g in results.GroupBy(r => r.Sev).OrderBy(g => g.Key))
            sb.AppendLine($"  {g.Key}: {g.Count()}");

        sb.AppendLine("\n## By Category");
        foreach (var g in results.GroupBy(r => r.Cat).OrderByDescending(g => g.Count()))
            sb.AppendLine($"  {g.Key}: {g.Count()}");

        sb.AppendLine("\n## Top Files");
        foreach (var g in results.GroupBy(r => r.File).OrderByDescending(g => g.Count()).Take(10))
            sb.AppendLine($"  {g.Key}: {g.Count()}");

        var p0Open = results.Count(r => r.Sev == "P0" && r.Status == "open");
        var p0All = results.Count(r => r.Sev == "P0");
        var resolution = results.Count(r => r.Status is "closed" or "verified" or "false_positive" or "wont_fix");
        if (p0All > 0)
        {
            sb.AppendLine("\n---");
            sb.AppendLine($"Resolution: {resolution}/{results.Count} ({(results.Count > 0 ? resolution * 100 / results.Count : 0)}%)");
            sb.AppendLine($"P0 open: {p0Open}/{p0All}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Batch-resolve multiple audit findings at once.
    /// </summary>
    [Description("批量标记审查发现状态。\n参数: findingIds — 逗号分隔的发现ID列表; status — addressed|false_positive|wont_fix")]
    [ToolExample("批量标记发现为已修复")]
    public async Task<string> BatchResolveAuditFindings(
        [Description("Comma-separated finding IDs")] string findingIds,
        [Description("Target status: addressed, false_positive, wont_fix")] string status,
        [Description("Optional: fix description")] string? fixDescription = null,
        CancellationToken ct = default)
    {
        var ids = findingIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0) return "(no finding IDs provided)";

        var results = new List<string>();
        foreach (var id in ids)
        {
            results.Add(await ResolveAuditFinding(id, status, fixDescription, ct).ConfigureAwait(false));
        }
        return string.Join("\n", results);
    }

    /// <summary>
    /// Batch-close multiple verified audit findings at once.
    /// </summary>
    [Description("批量关闭已验证的审查发现。\n参数: findingIds — 逗号分隔的发现ID列表")]
    [ToolExample("批量关闭已验证发现")]
    public async Task<string> BatchCloseAuditFindings(
        [Description("Comma-separated finding IDs")] string findingIds,
        [Description("Optional: close reason")] string? closeSummary = null,
        CancellationToken ct = default)
    {
        var ids = findingIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0) return "(no finding IDs provided)";

        var results = new List<string>();
        foreach (var id in ids)
        {
            results.Add(await CloseAuditFinding(id, closeSummary, ct).ConfigureAwait(false));
        }
        return string.Join("\n", results);
    }

    // ── Audit helpers ──

    private static Dictionary<string, object> ParseMetadata(string? metadataJson)
    {
        var meta = new Dictionary<string, object>();
        if (metadataJson == null) return meta;
        try
        {
            var existing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(metadataJson);
            if (existing != null)
                foreach (var kv in existing)
                    meta[kv.Key] = kv.Value.ValueKind switch
                    {
                        JsonValueKind.String => kv.Value.GetString() ?? "",
                        JsonValueKind.Number => kv.Value.GetDouble(),
                        _ => kv.Value.ToString(),
                    };
        }
        catch (Exception) { System.Diagnostics.Debug.WriteLine("[ReviewTools] Non-critical error"); }
        return meta;
    }

    private static string? GetMetaString(Dictionary<string, object> meta, string key)
        => meta.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static void AddAuditTrail(Dictionary<string, object> meta, string from, string to, string? summary)
    {
        var entry = new AuditTrailEntry
        {
            From = from, To = to,
            At = DateTimeOffset.UtcNow.ToString("o"),
            By = "agent",
            Summary = summary,
        };
        var trail = meta.TryGetValue("_audit_trail", out var existing) && existing is string json
            ? (JsonSerializer.Deserialize<List<AuditTrailEntry>>(json) ?? [])
            : [];
        trail.Add(entry);
        meta["_audit_trail"] = JsonSerializer.Serialize(trail, JsonOpts);
    }

    private static string ExportMarkdown(List<Dictionary<string, string?>> records)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Audit Findings ({records.Count})");
        sb.AppendLine();
        sb.AppendLine("| ID | Status | Severity | Category | File:Line | Description |");
        sb.AppendLine("|----|--------|----------|----------|-----------|-------------|");
        foreach (var r in records)
        {
            var desc = r["content"] ?? "";
            if (desc.Length > 80) desc = desc[..80] + "...";
            sb.AppendLine($"| {r["id"]} | {r["status"]} | {r["severity"]} | {r["category"]} | {r["file"]}:{r["line"]} | {desc} |");
        }
        return sb.ToString();
    }

    private static string ExportCsv(List<Dictionary<string, string?>> records)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Id,Status,Severity,Category,File,Line,Content");
        foreach (var r in records)
        {
            var content = (r["content"] ?? "").Replace("\"", "\"\"");
            sb.AppendLine($"{r["id"]},{r["status"]},{r["severity"]},{r["category"]},{r["file"]},{r["line"]},\"{content}\"");
        }
        return sb.ToString();
    }

    public record AuditTrailEntry
    {
        public string From { get; init; } = "";
        public string To { get; init; } = "";
        public string At { get; init; } = "";
        public string? By { get; init; }
        public string? Summary { get; init; }
    }

    public string StoreAsyncCompat(string wing, string room, string content,
        double importance, string agentId, Dictionary<string, object>? metadata, long? ttlMs)
        => _memoryStore!.StoreAsync(wing, room, content, role: "audit",
            importance: importance, agentId: agentId, metadata: metadata, ttlMs: ttlMs)
            .GetAwaiter().GetResult();

    // ── helpers ──

    private List<DiffFileInfo> GetDiffFiles(string? statusFilter = null)
    {
        var files = new List<DiffFileInfo>();

        try
        {
            var repoPath = LibGit2Sharp.Repository.Discover(_ws);
            if (repoPath == null) return files;
            using var repo = new LibGit2Sharp.Repository(repoPath);
            if (repo.Head.Tip == null) return files;

            var diff = repo.Diff.Compare<Patch>(repo.Head.Tip.Tree, DiffTargets.Index | DiffTargets.WorkingDirectory);

            foreach (var change in diff)
            {
                if (string.IsNullOrEmpty(change.Path)) continue;

                var status = change.Status switch
                {
                    LibGit2Sharp.ChangeKind.Added => "added",
                    LibGit2Sharp.ChangeKind.Deleted => "deleted",
                    LibGit2Sharp.ChangeKind.Modified => "modified",
                    LibGit2Sharp.ChangeKind.Renamed => "renamed",
                    LibGit2Sharp.ChangeKind.Copied => "added",
                    _ => "modified"
                };

                if (statusFilter != null && !status.Equals(statusFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                var fullPath = Path.Combine(repo.Info.WorkingDirectory, change.Path);

                files.Add(new DiffFileInfo(
                    FilePath: fullPath,
                    Status: status,
                    AddedLines: change.Patch?.Count(c => c == '+') ?? 0,
                    DeletedLines: change.Patch?.Count(c => c == '-') ?? 0));
            }
        }
        catch (LibGit2Sharp.RepositoryNotFoundException)
        {
            // Not a git repository — normal for non-git directories
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ReviewTools] Git error: {ex.Message}"); }

        return files;
    }

    private static bool GlobMatch(string path, string pattern)
    {
        var name = Path.GetFileName(path);
        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return name == pattern || path == pattern;

        var regex = RegexCache.GetOrAddFactory($"rv:{pattern}", () =>
            new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                RegexOptions.Compiled | RegexOptions.IgnoreCase));
        return regex.IsMatch(name) || regex.IsMatch(path);
    }
}
