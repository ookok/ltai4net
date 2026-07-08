// Copyright (c) LTAI. All rights reserved.
// ──────────────────────────────────────────────────────────────
//  GrammarCheckStep — 生成时语法检查前置
//
//  Main file: fields, constructor, ProcessAsync.
//  Split across partial files for maintainability:
//    GrammarCheckStep.QuickParse.cs  — QuickParseFile/CSharp/TreeSitter + DetectTsErrors
//    GrammarCheckStep.Validation.cs — ExtractedFiles/Paths, BuildMessages, RuleEngine helpers,
//                                     CLR validators (ValidateImports/ApiClaims/ConfigClaims)
//    GrammarCheckStep.Models.cs     — GrammarError, GrammarErrorSeverity, GrammarCheckOptions
// ──────────────────────────────────────────────────────────────

using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using LTAI.Agent.Delta;
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
public sealed partial class GrammarCheckStep : IPipelineStep
{
    private readonly ILogger<GrammarCheckStep> _logger;
    private readonly string _workspacePath;
    private readonly TreeSitterParser? _tsParser;
    private readonly ReviewRuleEngine? _ruleEngine;
    private readonly LspLanguageManager? _lspManager;
    private readonly GrammarCheckOptions _options;
    private readonly DeltaStore? _deltaStore;
    private readonly string? _currentConversationId;
    private readonly string? _currentMessageId;

    public string Name => "GrammarCheck";

    public GrammarCheckStep(
        ILogger<GrammarCheckStep>? logger = null,
        string? workspacePath = null,
        TreeSitterParser? tsParser = null,
        ReviewRuleEngine? ruleEngine = null,
        LspLanguageManager? lspManager = null,
        GrammarCheckOptions? options = null,
        DeltaStore? deltaStore = null,
        string? currentConversationId = null,
        string? currentMessageId = null)
    {
        _logger = logger ?? NullLogger<GrammarCheckStep>.Instance;
        _workspacePath = workspacePath ?? Directory.GetCurrentDirectory();
        _tsParser = tsParser;
        _ruleEngine = ruleEngine ?? new ReviewRuleEngine();
        if (_ruleEngine.Rules.Count == 0)
        {
            _ruleEngine.LoadBuiltinRules();
            LoadMinedRules(_ruleEngine);
        }
        _lspManager = lspManager;
        _options = options ?? new GrammarCheckOptions();
        _deltaStore = deltaStore;
        _currentConversationId = currentConversationId;
        _currentMessageId = currentMessageId;
    }

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        if (_options.EnableQuickParse)
        {
            var writtenFiles = ExtractWrittenFiles(context);

            if (writtenFiles.Count > 0)
            {
                var syntaxErrors = new List<GrammarError>();
                var typeErrors = new List<GrammarError>();

                // 第 1 层: AST 解析 (Roslyn / TreeSitter)
                foreach (var filePath in writtenFiles)
                {
                    var content = File.ReadAllText(filePath);
                    var fileErrors = QuickParseFile(filePath, content);

                    foreach (var err in fileErrors)
                    {
                        if (err.Severity == GrammarErrorSeverity.Error)
                            syntaxErrors.Add(err);
                        else
                            typeErrors.Add(err);
                    }
                }

                // 第 2 层: RuleEngine check — 真实规则匹配（仅当文件内容命中规则时才报告）
                if (_options.EnableRuleEngine && _ruleEngine != null && syntaxErrors.Count == 0)
                {
                    foreach (var filePath in writtenFiles)
                    {
                        var content = File.ReadAllText(filePath);
                        foreach (var m in _ruleEngine.Match(filePath, content))
                        {
                            typeErrors.Add(new GrammarError(filePath, m.LineNumber, 0,
                                MapRuleSeverity(m.Severity), "rule", m.RuleId, m.Message, "ReviewRuleEngine"));
                        }
                    }
                }

                // 第 3 层: CLR (Claim-Level Reliability) — cross-reference imports, URLs, config keys
                if (_options.EnableClr && syntaxErrors.Count == 0)
                {
                    foreach (var filePath in writtenFiles)
                    {
                        var content = File.ReadAllText(filePath);
                        typeErrors.AddRange(ValidateImports(filePath, content));
                        typeErrors.AddRange(ValidateApiClaims(content));
                        typeErrors.AddRange(ValidateConfigClaims(content));
                    }
                }

                // 第 4 层: LSP diagnostics (multi-language, real diagnostics from running servers)
                if (_options.EnableLspDiag && syntaxErrors.Count == 0 && _lspManager != null)
                {
                    try
                    {
                        var lspDiags = _lspManager.GetDiagnostics();
                        foreach (var (filePath, diag) in lspDiags)
                        {
                            if (!writtenFiles.Contains(filePath, StringComparer.OrdinalIgnoreCase))
                                continue;
                            var sev = diag.Severity switch
                            {
                                1 => GrammarErrorSeverity.Error,
                                2 => GrammarErrorSeverity.Warning,
                                _ => GrammarErrorSeverity.Info,
                            };
                            typeErrors.Add(new GrammarError(filePath, diag.Line, diag.Col,
                                sev, "lsp", diag.Code ?? "LSP", diag.Message, diag.Source ?? "LSP"));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "LSP diagnostics unavailable");
                    }
                }

                // 存储到 MessageContext 供下游步骤使用
                context.Set("GrammarErrors", syntaxErrors.Concat(typeErrors).ToList());

                // 构建注入上下文
                var deltaMap = _deltaStore?.GetType().GetMethod("GetDeltaMap")?.Invoke(_deltaStore,
                    [_currentConversationId ?? "", _currentMessageId ?? ""]) as Dictionary<string, string>;
                var injectedMessages = BuildGrammarErrorMessages(writtenFiles, syntaxErrors, typeErrors, deltaMap);
                foreach (var msg in injectedMessages)
                {
                    lock (context.MessagesLock) context.Messages.Add(msg);
                }

                if (syntaxErrors.Count > 0)
                {
                    context.GrammarCheckBlocked = true;
                    context.Set("GrammarCheckReason",
                        $"发现 {syntaxErrors.Count} 个语法错误，已注入上下文等待修复");
                    _logger.LogWarning(
                        "GrammarCheckStep: {ErrCount} syntax errors found in {FileCount} files, blocking generation",
                        syntaxErrors.Count, writtenFiles.Count);
                }
            }
        }

        return context;
    }
}
