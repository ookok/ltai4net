using System.ComponentModel;
using System.Text;
using LTAI.AI;
using LibGit2Sharp;

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

    public ReviewTools(string ws)
    {
        _ws = ws;
        _ruleEngine = new ReviewRuleEngine();
        _grouping = new DiffGroupingAnalyzer();
        _positioner = new ExternalPositioner();
        _reflector = new ReviewReflector();

        // Load rules
        _ruleEngine.LoadBuiltinRules();
        try
        {
            _ruleEngine.LoadProjectRules(_ws);
        }
        catch
        {
            // Project rules are optional
        }
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
            var rules = System.Text.Json.JsonSerializer.Deserialize<List<ReviewRule>>(rulesJson);
            if (rules == null || rules.Count == 0)
                return "No rules parsed from input.";

            _ruleEngine.AddRules(rules);
            return $"Loaded {rules.Count} custom review rules. Total rules: {_ruleEngine.Rules.Count}";
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

                if (File.Exists(file.FilePath))
                {
                    fileContents[file.FilePath] = File.ReadAllText(file.FilePath);
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
                if (File.Exists(file.FilePath))
                    fileContents[file.FilePath] = File.ReadAllText(file.FilePath);
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
        catch
        {
            // If no git repo or other issue, return empty
        }

        return files;
    }

    private static bool GlobMatch(string path, string pattern)
    {
        var name = Path.GetFileName(path);
        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return name == pattern || path == pattern;

        var regex = new System.Text.RegularExpressions.Regex(
            "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace("\\*", ".*").Replace("\\?", ".") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return regex.IsMatch(name) || regex.IsMatch(path);
    }
}
