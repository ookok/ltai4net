// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  GrammarCheckStep — 生成时语法检查前置
//
//  在 Agent 写入/编辑代码文件后**立即**执行轻量语法检查，
//  将语法错误发现从「构建时」前置到「生成时」。
//
//  三层检查（按速度优先排序）:
//    第 1 层: QuickParseCheck  <200ms  —  Roslyn/TreeSitter AST 解析
//    第 2 层: RuleEngineCheck  <300ms  —  确定性规则匹配（复用 ReviewRuleEngine）
//    第 3 层: LspDiagCheck     <500ms  —  LSP 增量语义诊断（复用 LspLanguageManager）
//
//  决策机制:
//    - 语法错误 (SyntaxError) → 打断生成 + 注入错误上下文 + 自动修复
//    - 语义警告 (Warning)     → 继续生成但标记供审查
//    - 规则匹配 (RuleMatch)    → 注入警告到上下文（不打断）
//
//  Usage: instantiated directly in ChatAgent or via DI.
//    var step = new GrammarCheckStep(tsParser, lspManager);
// ═══════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using LTAI.Agent.LanguageServer;
using LTAI.Agent.Tools;
using LTAI.Agent.CodeAnalysis;
using LTAI.Agent.Tools.Review;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Pipeline.Steps;

/// <summary>
/// 生成时语法检查步骤。自动检测所有已写入的文件并做三层检查。
/// </summary>
public sealed class GrammarCheckStep : IPipelineStep
{
    private readonly ILogger<GrammarCheckStep> _logger;
    private readonly string _workspacePath;
    private readonly TreeSitterParser? _tsParser;
    private readonly ReviewRuleEngine? _ruleEngine;
    private readonly LspLanguageManager? _lspManager;
    private readonly GrammarCheckOptions _options;

    public string Name => "GrammarCheck";

    public GrammarCheckStep(
        ILogger<GrammarCheckStep>? logger = null,
        string? workspacePath = null,
        TreeSitterParser? tsParser = null,
        ReviewRuleEngine? ruleEngine = null,
        LspLanguageManager? lspManager = null,
        GrammarCheckOptions? options = null)
    {
        _logger = logger ?? NullLogger<GrammarCheckStep>.Instance;
        _workspacePath = workspacePath ?? Directory.GetCurrentDirectory();
        _tsParser = tsParser;
        _ruleEngine = ruleEngine ?? new ReviewRuleEngine();
        // Ensure built-in rules are loaded (including any from FailureMiner)
        if (_ruleEngine.Rules.Count == 0)
        {
            _ruleEngine.LoadBuiltinRules();
            // Also load mined rules from FailureMiner if available
            LoadMinedRules(_ruleEngine);
        }
        _lspManager = lspManager;
        _options = options ?? new GrammarCheckOptions();
    }

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        // ── 第 0 步: 从 ToolCalls 中提取本轮回合写入的文件 ──
        var writtenFiles = ExtractWrittenFiles(context);
        if (writtenFiles.Count == 0)
            return context;

        _logger.LogInformation("GrammarCheckStep: checking {Count} written files", writtenFiles.Count);

        var allErrors = new List<GrammarError>();

        // ── 第 1 层: QuickParseCheck（最快） ──
        if (_options.EnableQuickParse)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var filePath in writtenFiles)
            {
                try
                {
                    // Guard against loading arbitrarily large files written by agent
                    const long maxFileSize = 10 * 1024 * 1024; // 10 MB
                    var fi = new FileInfo(filePath);
                    if (fi.Exists && fi.Length > maxFileSize)
                    {
                        allErrors.Add(new GrammarError(filePath, 0, 0,
                            GrammarErrorSeverity.Warning, "grammar_check", "FILE_TOO_LARGE",
                            $"File too large for grammar check ({fi.Length / 1024 / 1024}MB, max {maxFileSize / 1024 / 1024}MB)",
                            "GrammarCheckStep"));
                        continue;
                    }
                    var content = await File.ReadAllTextAsync(filePath, context.CancellationToken)
                        .ConfigureAwait(false);
                    var errors = QuickParseFile(filePath, content);
                    allErrors.AddRange(errors);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "GrammarCheckStep: quick-parse failed for {File}", filePath);
                }
            }
            sw.Stop();
            _logger.LogDebug("GrammarCheckStep: QuickParse completed in {Ms:F1}ms, {ErrCount} errors",
                sw.Elapsed.TotalMilliseconds, allErrors.Count(e => e.IsError));
        }

        // ── 第 2 层: RuleEngineCheck（确定性规则） ──
        if (_options.EnableRuleEngine && _ruleEngine != null && _ruleEngine.Rules.Count > 0)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var fileContents = new Dictionary<string, string>();
            foreach (var filePath in writtenFiles)
            {
                try
                {
                    fileContents[filePath] = await File.ReadAllTextAsync(filePath, context.CancellationToken)
                        .ConfigureAwait(false);
                }
                catch { /* skip unreadable */ }
            }

            var ruleMatches = _ruleEngine.MatchAll(fileContents);
            foreach (var (filePath, matches) in ruleMatches)
            {
                foreach (var m in matches)
                {
                    allErrors.Add(new GrammarError(
                        File: filePath,
                        Line: m.LineNumber,
                        Column: 0,
                        Severity: MapRuleSeverity(m.Severity),
                        Category: "rule",
                        Code: m.RuleId,
                        Message: m.Message,
                        Source: "ReviewRuleEngine"
                    ));
                }
            }
            sw.Stop();
            _logger.LogDebug("GrammarCheckStep: RuleEngine completed in {Ms:F1}ms, {MatchCount} matches",
                sw.Elapsed.TotalMilliseconds, ruleMatches.Sum(kv => kv.Value.Count));
        }

        // ── 第 3 层: LSP Diagnostics（语义诊断） ──
        if (_options.EnableLspDiag && _lspManager != null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var filePath in writtenFiles)
            {
                try
                {
                    // Notify LSP of file changes
                    var ext = Path.GetExtension(filePath);
                    if (LspLanguageManager.HasLsp(ext))
                    {
                        var content = await File.ReadAllTextAsync(filePath, context.CancellationToken)
                            .ConfigureAwait(false);
                        await _lspManager.UpdateFileAsync(filePath, content, context.CancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "GrammarCheckStep: LSP update failed for {File}", filePath);
                }
            }

            var lspDiags = _lspManager.GetDiagnostics();
            foreach (var (filePath, diag) in lspDiags)
            {
                if (!writtenFiles.Contains(filePath, StringComparer.OrdinalIgnoreCase))
                    continue;

                allErrors.Add(new GrammarError(
                    File: filePath,
                    Line: diag.Line + 1,
                    Column: diag.Col + 1,
                    Severity: diag.IsError ? GrammarErrorSeverity.Error
                        : diag.IsWarning ? GrammarErrorSeverity.Warning
                        : GrammarErrorSeverity.Info,
                    Category: "lsp",
                    Code: diag.Code ?? "",
                    Message: diag.Message,
                    Source: diag.Source ?? "LSP"
                ));
            }
            sw.Stop();
            _logger.LogDebug("GrammarCheckStep: LSP diagnostics completed in {Ms:F1}ms, {DiagCount} diagnostics",
                sw.Elapsed.TotalMilliseconds, lspDiags.Count);
        }

        // ── 汇总结果 ──
        if (allErrors.Count == 0)
        {
            _logger.LogInformation("GrammarCheckStep: ✅ all clean for {Count} files", writtenFiles.Count);
            return context;
        }

        // 按严重度分组
        var syntaxErrors = allErrors.Where(e => e.Severity == GrammarErrorSeverity.Error).ToList();
        var warnings = allErrors.Where(e => e.Severity == GrammarErrorSeverity.Warning).ToList();

        // 存储到 MessageContext 供下游步骤使用
        context.Set("GrammarErrors", allErrors);

        // 构建注入上下文
        var injectedMessages = BuildGrammarErrorMessages(writtenFiles, syntaxErrors, warnings);
        foreach (var msg in injectedMessages)
        {
            // 插入到用户消息之后，但 tool 结果之前
            context.Messages.Add(msg);
        }

        // 如果有语法错误，打断生成
        if (syntaxErrors.Count > 0)
        {
            context.Set("GrammarCheckBlocked", true);
            context.Set("GrammarCheckReason",
                $"发现 {syntaxErrors.Count} 个语法错误，已注入上下文等待修复");

            _logger.LogWarning(
                "GrammarCheckStep: ❌ {ErrCount} syntax errors found in {FileCount} files, blocking generation",
                syntaxErrors.Count, writtenFiles.Count);
        }
        else
        {
            _logger.LogInformation(
                "GrammarCheckStep: ⚠️ {WarnCount} warnings injected (non-blocking)",
                warnings.Count);
        }

        // ── 审核跟踪记录 ──
        try
        {
            var auditDir = Path.Combine(AppContext.BaseDirectory, ".livingtree", "audit");
            Directory.CreateDirectory(auditDir);
            var auditPath = Path.Combine(auditDir, "grammar-check.jsonl");
            var auditRecord = new System.Text.StringBuilder();
            auditRecord.Append(System.Text.Json.JsonSerializer.Serialize(new
            {
                Timestamp = DateTime.UtcNow.ToString("O"),
                ProcessId = Environment.ProcessId,
                MachineName = Environment.MachineName,
                FilesChecked = writtenFiles.Select(f => Path.GetRelativePath(_workspacePath, f)).ToList(),
                ErrorCount = syntaxErrors.Count,
                WarningCount = warnings.Count,
                IsBlocked = syntaxErrors.Count > 0,
                Summary = syntaxErrors.Count > 0
                    ? $"Blocked: {syntaxErrors.Count} errors in {writtenFiles.Count} files"
                    : $"Passed: {warnings.Count} warnings in {writtenFiles.Count} files"
            }));
            auditRecord.AppendLine();
            File.AppendAllText(auditPath, auditRecord.ToString());
        }
        catch { /* audit failure is non-critical */ }

        return context;
    }

    // ═══════════════════════════════════════════════════════════
    //  第 1 层: QuickParse — 轻量语法分析
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 根据文件扩展名选择解析器进行快速语法检查。
    /// C# 用 Roslyn, 其他语言用 TreeSitter。
    /// </summary>
    private List<GrammarError> QuickParseFile(string filePath, string content)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        return ext switch
        {
            ".cs" => QuickParseCSharp(filePath, content),
            ".py" or ".js" or ".jsx" or ".ts" or ".tsx"
                or ".go" or ".rs" or ".java"
                or ".sh" or ".bash" or ".json" or ".html" or ".css"
                or ".mbt" or ".mojo" or ".cj" or "🔥"
                => QuickParseTreeSitter(filePath, content, ext),
            _ => [] // 不支持的语言跳过
        };
    }

    /// <summary>
    /// 使用 Roslyn CSharpSyntaxTree 对 .cs 文件做快速语法解析。
    /// 只做 Parse（不编译），捕获 SyntaxKind 级别的语法错误。
    /// 典型耗时: 30-80ms / 文件。
    /// </summary>
    private static List<GrammarError> QuickParseCSharp(string filePath, string content)
    {
        var errors = new List<GrammarError>();

        try
        {
            var tree = CSharpSyntaxTree.ParseText(content, options: null, filePath);
            var diagnostics = tree.GetDiagnostics();

            foreach (var diag in diagnostics)
            {
                if (diag.Severity == DiagnosticSeverity.Hidden)
                    continue;

                var lineSpan = diag.Location.GetLineSpan();
                errors.Add(new GrammarError(
                    File: filePath,
                    Line: lineSpan.StartLinePosition.Line + 1,
                    Column: lineSpan.StartLinePosition.Character + 1,
                    Severity: diag.Severity switch
                    {
                        DiagnosticSeverity.Error => GrammarErrorSeverity.Error,
                        DiagnosticSeverity.Warning => GrammarErrorSeverity.Warning,
                        _ => GrammarErrorSeverity.Info
                    },
                    Category: "syntax",
                    Code: diag.Id,
                    Message: diag.GetMessage(),
                    Source: "Roslyn"
                ));
            }
        }
        catch (Exception ex)
        {
            // Roslyn 解析异常（如文件编码问题）
            errors.Add(new GrammarError(
                File: filePath,
                Line: 1, Column: 1,
                Severity: GrammarErrorSeverity.Error,
                Category: "syntax",
                Code: "ROSLYN-FATAL",
                Message: $"Roslyn parse failed: {ex.Message}",
                Source: "Roslyn"
            ));
        }

        return errors;
    }

    /// <summary>
    /// 使用 TreeSitter 对其他语言做快速语法解析。
    /// TreeSitter 是增量解析器，天生适合流式检查。
    /// 典型耗时: 50-150ms / 文件。
    /// </summary>
    private List<GrammarError> QuickParseTreeSitter(string filePath, string content, string ext)
    {
        var errors = new List<GrammarError>();

        if (_tsParser == null)
        {
            _logger.LogDebug("GrammarCheckStep: TreeSitterParser not available, skipping {File}", filePath);
            return errors;
        }

        try
        {
            var tree = _tsParser.Parse(content, ext);
            if (tree == null)
            {
                // 不支持的语言
                return errors;
            }

            // TreeSitter 本身不提供语法错误列表，但我们可以检查 root node 是否有错误子节点
            var rootNode = tree.RootNode;
            if (rootNode == null)
                return errors;

            // 检测解析错误: TreeSitter 用 ERROR 和 MISSING 节点表示语法错误
            DetectTsErrors(rootNode, filePath, content, errors);
        }
        catch (Exception ex)
        {
            errors.Add(new GrammarError(
                File: filePath,
                Line: 1, Column: 1,
                Severity: GrammarErrorSeverity.Error,
                Category: "syntax",
                Code: "TS-FATAL",
                Message: $"TreeSitter parse failed: {ex.Message}",
                Source: "TreeSitter"
            ));
        }

        return errors;
    }

    /// <summary>
    /// 递归检测 TreeSitter AST 中的 ERROR 和 MISSING 节点。
    /// ERROR 节点表示解析器无法匹配的语法区域。
    /// MISSING 节点表示期望但缺失的语法元素。
    /// </summary>
    private static void DetectTsErrors(
        global::TreeSitter.Node node,
        string filePath,
        string content,
        List<GrammarError> errors,
        HashSet<(int line, int col)>? seen = null)
    {
        seen ??= new HashSet<(int, int)>();

        // 检测 ERROR 节点 — 语法错误区域
        if (node.Type == "ERROR")
        {
            var line = node.StartPosition.Row + 1;
            var col = node.StartPosition.Column + 1;

            if (seen.Add((line, col)))
            {
                // 提取错误附近的源码片段（最多 40 个字符）
                var snippet = ExtractSnippet(content, node.StartPosition.Row, 40);

                errors.Add(new GrammarError(
                    File: filePath,
                    Line: line,
                    Column: col,
                    Severity: GrammarErrorSeverity.Error,
                    Category: "syntax",
                    Code: "TS-ERROR",
                    Message: $"语法错误：无法解析此处代码。上下文: \"{snippet}\"",
                    Source: "TreeSitter"
                ));
            }
        }

        // 检测 MISSING 节点 — 期望但缺失的元素
        if (node.IsMissing)
        {
            var line = node.StartPosition.Row + 1;
            var col = node.StartPosition.Column + 1;

            if (seen.Add((line, col)))
            {
                errors.Add(new GrammarError(
                    File: filePath,
                    Line: line,
                    Column: col,
                    Severity: GrammarErrorSeverity.Error,
                    Category: "syntax",
                    Code: "TS-MISSING",
                    Message: $"语法错误：期望 \"{node.Text ?? "?"}\" 但未找到",
                    Source: "TreeSitter"
                ));
            }
        }

        // 递归子节点
        foreach (var child in node.Children)
        {
            DetectTsErrors(child, filePath, content, errors, seen);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  辅助方法
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 从 ToolCalls 中提取所有被写入的文件路径。
    /// 支持 write/edit 操作，去重，只保留在工作区内的路径。
    /// </summary>
    private HashSet<string> ExtractWrittenFiles(MessageContext context)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, args, _) in context.ToolCalls)
        {
            foreach (var path in ExtractPathsFromArgs(name, args))
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    files.Add(path);
                }
            }
        }

        return files;
    }

    /// <summary>
    /// 从工具调用参数中提取文件路径。
    /// 支持 key=value 和 JSON 两种格式。
    /// </summary>
    private static IEnumerable<string> ExtractPathsFromArgs(string toolName, string args)
    {
        var paths = new List<string>();

        // 格式 1: path=xxx 或 filePath=xxx (引号可选)
        var matches = PipelineRegex.PathArgPattern().Matches(args);

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            if (match.Success)
            {
                var path = match.Groups[1].Value;
                if (!string.IsNullOrEmpty(path))
                    paths.Add(path);
            }
        }

        // 格式 2: JSON 格式 "path":"value" 或 "filePath":"value"
        var jsonMatches = PipelineRegex.JsonPathArgPattern().Matches(args);

        foreach (System.Text.RegularExpressions.Match match in jsonMatches)
        {
            if (match.Success)
            {
                var path = match.Groups[1].Value;
                if (!string.IsNullOrEmpty(path))
                    paths.Add(path);
            }
        }

        return paths;
    }

    /// <summary>
    /// 构建用于注入 agent 上下文的语法错误信息。
    /// 语法错误 → 注入到 Messages 头部让 LLM 看到并要求修复
    /// 警告 → 注入到 Messages 尾部作为参考
    /// </summary>
    private static List<Microsoft.Extensions.AI.ChatMessage> BuildGrammarErrorMessages(
        HashSet<string> writtenFiles,
        List<GrammarError> syntaxErrors,
        List<GrammarError> warnings)
    {
        var messages = new List<Microsoft.Extensions.AI.ChatMessage>();

        // 如果只有警告没有语法错误，只注入一条 summary
        if (syntaxErrors.Count == 0 && warnings.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## ⚠️ 代码质量提示");
            foreach (var group in warnings.GroupBy(e => e.File))
            {
                sb.AppendLine($"\n### {group.Key}");
                foreach (var e in group)
                {
                    sb.AppendLine($"  L{e.Line,-5} [{e.Code}] {e.Message}");
                }
            }
            sb.AppendLine("\n以上为建议性提示，不影响编译。请酌情处理。");
            messages.Add(new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.System, sb.ToString()));
            return messages;
        }

        // 有语法错误 — 构建详细修复指引
        var fixSb = new StringBuilder();
        fixSb.AppendLine("## ❌ 语法错误 — 请立即修复");
        fixSb.AppendLine();
        fixSb.AppendLine("以下文件包含语法错误，**请自动修复后再继续**：");
        fixSb.AppendLine();

        foreach (var group in syntaxErrors.GroupBy(e => e.File))
        {
            fixSb.AppendLine($"### {group.Key} ({group.Count()} 个错误)");
            fixSb.AppendLine();
            foreach (var e in group)
            {
                fixSb.AppendLine($"  L{e.Line}:{e.Column} [{e.Code}] {e.Message}");
            }
            fixSb.AppendLine();
        }

        fixSb.AppendLine("### 修复指引");
        fixSb.AppendLine("1. 使用 edit 工具修正上述语法错误");
        fixSb.AppendLine("2. 修正后继续执行原任务");
        fixSb.AppendLine("3. 如不确定修复方式，可以询问用户");

        messages.Add(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.System, fixSb.ToString()));

        // 附带警告信息（如果有）
        if (warnings.Count > 0)
        {
            var warnSb = new StringBuilder();
            warnSb.AppendLine("## ⚠️ 附带代码质量提示");
            foreach (var group in warnings.GroupBy(e => e.File))
            {
                warnSb.AppendLine($"\n### {group.Key}");
                foreach (var e in group)
                {
                    warnSb.AppendLine($"  L{e.Line,-5} [{e.Code}] {e.Message}");
                }
            }
            messages.Add(new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.System, warnSb.ToString()));
        }

        return messages;
    }

    private static GrammarErrorSeverity MapRuleSeverity(string severity) => severity.ToLowerInvariant() switch
    {
        "error" => GrammarErrorSeverity.Error,
        "warning" => GrammarErrorSeverity.Warning,
        "info" => GrammarErrorSeverity.Info,
        _ => GrammarErrorSeverity.Info
    };

    /// <summary>从源码的某一行提取片段（用于错误上下文显示）。</summary>
    private static string ExtractSnippet(string content, int row, int maxLen)
    {
        var lines = content.Split('\n');
        if (row >= 0 && row < lines.Length)
        {
            var line = lines[row].Trim();
            return line.Length <= maxLen ? line : line[..maxLen] + "...";
        }
        return "?";
    }

    /// <summary>Load mined rules from FailureMiner (mined-rules.json).</summary>
    private static void LoadMinedRules(ReviewRuleEngine engine)
    {
        try
        {
            var minedPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".livingtree", "learn", "mined-rules.json");
            if (!File.Exists(minedPath)) return;

            var json = File.ReadAllText(minedPath);
            var mined = System.Text.Json.JsonSerializer.Deserialize<MinedRulesFile>(json);
            if (mined?.rules == null || mined.rules.Count == 0) return;

            foreach (var rule in mined.rules)
            {
                if (!string.IsNullOrEmpty(rule.Pattern))
                {
                    engine.AddRule(new ReviewRule
                    {
                        Id = $"MINE-{rule.Title.GetHashCode():x8}",
                        Name = rule.Title,
                        Category = "mined",
                        Severity = "warning",
                        Description = rule.Description,
                        Pattern = rule.Pattern,
                        MessageTemplate = rule.Description
                    });
                }
            }
        }
        catch { /* best-effort: mined rules are non-critical */ }
    }

    private sealed record MinedRulesFile(DateTime MinedAt, int TotalFailures, System.Collections.Generic.List<MinedRuleEntry> rules);
    private sealed record MinedRuleEntry(string Title, string Description, string Pattern, int Frequency);
}

// ═══════════════════════════════════════════════════════════════
//  数据结构
// ═══════════════════════════════════════════════════════════════

/// <summary>语法检查错误/警告的记录。</summary>
/// <param name="File">文件路径</param>
/// <param name="Line">行号 (1-based)</param>
/// <param name="Column">列号 (1-based)</param>
/// <param name="Severity">严重度</param>
/// <param name="Category">类别: syntax, rule, lsp</param>
/// <param name="Code">错误码</param>
/// <param name="Message">错误信息</param>
/// <param name="Source">来源: Roslyn, TreeSitter, ReviewRuleEngine, LSP</param>
public sealed record GrammarError(
    string File,
    int Line,
    int Column,
    GrammarErrorSeverity Severity,
    string Category,
    string Code,
    string Message,
    string Source)
{
    /// <summary>是否为语法错误（会打断生成）。</summary>
    public bool IsError => Severity == GrammarErrorSeverity.Error;
}

/// <summary>语法检查的严重度级别。</summary>
public enum GrammarErrorSeverity
{
    /// <summary>致命语法错误，必须修复</summary>
    Error,
    /// <summary>可能的问题，建议修复</summary>
    Warning,
    /// <summary>信息提示</summary>
    Info,
}

/// <summary>GrammarCheckStep 的配置选项。</summary>
public sealed class GrammarCheckOptions
{
    /// <summary>是否启用 QuickParse 层（Roslyn/TreeSitter）。默认 true。</summary>
    public bool EnableQuickParse { get; set; } = true;

    /// <summary>是否启用 RuleEngine 层（确定性规则）。默认 true。</summary>
    public bool EnableRuleEngine { get; set; } = true;

    /// <summary>是否启用 LSP 诊断层。默认 true（C# 使用内置 Roslyn 无需进程）。</summary>
    public bool EnableLspDiag { get; set; } = true;
}

