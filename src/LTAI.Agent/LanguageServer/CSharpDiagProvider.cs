// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  CSharpDiagProvider — C# 语义诊断（替代 LSP 进程）
//
//  不使用 LSP 进程（如 roslyn-lsp），而是直接通过 Roslyn
//  Compilation API 对 .cs 文件做语义分析，获取完整的类型检查、
//  符号解析、语法错误等诊断信息。
//
//  优势:
//    - 不需要外部 LSP 进程（无启动延迟、无崩溃风险）
//    - 增量编译缓存（同一项目多次编辑不会重复解析所有文件）
//    - 与 GrammarCheckStep 的 QuickParseCSharp 互补:
//      它只做 Parse（语法级），本组件做 Compilation（语义级）
//
//  典型耗时:
//    - 首次编译: 200-500ms（加载 .NET BCL 引用）
//    - 增量编译: 10-30ms（只重新解析修改的文件）
// ═══════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.LanguageServer;

/// <summary>
/// 为 C# 文件提供语义级诊断，替代 LSP 进程。
/// 管理内存中的 Roslyn Workspace，支持增量更新。
/// </summary>
public sealed class CSharpDiagProvider : IDisposable
{
    private readonly ILogger<CSharpDiagProvider> _logger;
    private readonly AdhocWorkspace _workspace;
    private readonly Project _project;
    private readonly ConcurrentDictionary<string, Document> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _compileLock = new(1, 1);
    private Compilation? _lastCompilation;
    private bool _disposed;

    /// <summary>最后一次编译是否有错误。</summary>
    public bool HasErrors { get; private set; }

    /// <summary>最近一次诊断结果缓存。</summary>
    public IReadOnlyList<LspDiagnostic> LastDiagnostics => _lastDiagnostics;
    private List<LspDiagnostic> _lastDiagnostics = [];

    /// <summary>可访问的工作区（供外部读取 source info）。</summary>
    public Workspace Workspace => _workspace;

    public CSharpDiagProvider(ILogger<CSharpDiagProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<CSharpDiagProvider>.Instance;
        _workspace = new AdhocWorkspace();

        // 创建一个 "无项目" 的解决方案，每个 .cs 文件通过 OpenDocumentAsync 动态加入
        var solution = _workspace.CurrentSolution;
        var project = solution.AddProject("LTAI-Dynamic", "LTAI-Dynamic", LanguageNames.CSharp);

        // 添加核心 .NET 引用（从当前运行时加载）
        project = project.WithCompilationOptions(
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable));

        // 添加基本的元数据引用
        foreach (var assembly in GetDefaultAssemblies())
        {
            project = project.AddMetadataReference(assembly);
        }

        _project = project;

        if (!_workspace.TryApplyChanges(_project.Solution))
        {
            _logger.LogWarning("CSharpDiagProvider: failed to apply initial workspace changes");
        }
    }

    /// <summary>
    /// 打开（或更新）一个 .cs 文件并获取诊断。
    /// </summary>
    public async Task<List<LspDiagnostic>> UpdateDocumentAsync(
        string filePath, string content, CancellationToken ct = default)
    {
        if (_disposed) return [];

        try
        {
            await _compileLock.WaitAsync(ct).ConfigureAwait(false);

            var sourceText = SourceText.From(content);

            if (_documents.TryGetValue(filePath, out var existingDoc))
            {
                // 更新已存在的文档
                _workspace.TryApplyChanges(
                    existingDoc.WithText(sourceText).Project.Solution);
            }
            else
            {
                // 添加新文档
                var doc = _workspace.AddDocument(_project.Id, Path.GetFileName(filePath), sourceText);
                _documents[filePath] = doc;

                // 更新 _project 引用指向新的 project
                // 注意: AddDocument 会返回新的 Document，属于新的 Solution
                var newDoc = _workspace.CurrentSolution.GetDocument(doc.Id);
                if (newDoc != null)
                {
                    _documents[filePath] = newDoc;
                }
            }

            // 使用当前解决方案重新编译
            var solution = _workspace.CurrentSolution;
            var project = solution.GetProject(_project.Id);
            if (project == null)
            {
                _logger.LogWarning("CSharpDiagProvider: project not found after update");
                return [];
            }

            var compilation = await project
                .GetCompilationAsync(ct)
                .ConfigureAwait(false);

            if (compilation == null)
            {
                _logger.LogWarning("CSharpDiagProvider: compilation returned null");
                return [];
            }

            _lastCompilation = compilation;

            // 获取所有诊断
            var allDiags = compilation.GetDiagnostics(ct);
            var diagnostics = new List<LspDiagnostic>();

            foreach (var diag in allDiags)
            {
                if (diag.Severity == DiagnosticSeverity.Hidden)
                    continue;

                // 只报告当前文件（或者其他文件的引用错误）
                // 如果文件路径不匹配但级别是 Error，也报告（跨文件类型错误）
                var diagFilePath = diag.Location.GetLineSpan().Path;
                var isCurrentFile = string.IsNullOrEmpty(diagFilePath) ||
                    diagFilePath.Replace('\\', '/').EndsWith(
                        filePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

                // 对于 Error 级别，即使跨文件也报告（如缺失类型定义）
                if (!isCurrentFile && diag.Severity != DiagnosticSeverity.Error)
                    continue;

                var lineSpan = diag.Location.GetLineSpan();
                diagnostics.Add(new LspDiagnostic(
                    Severity: diag.Severity switch
                    {
                        DiagnosticSeverity.Error => 1,
                        DiagnosticSeverity.Warning => 2,
                        _ => 3,
                    },
                    Message: diag.GetMessage(),
                    Line: lineSpan.StartLinePosition.Line,
                    Col: lineSpan.StartLinePosition.Character,
                    Code: diag.Id,
                    Source: "Roslyn"
                ));
            }

            HasErrors = diagnostics.Any(d => d.IsError);
            _lastDiagnostics = diagnostics;

            _logger.LogDebug("CSharpDiagProvider: {File} → {Count} diagnostics ({Errors} errors)",
                Path.GetFileName(filePath),
                diagnostics.Count,
                diagnostics.Count(d => d.IsError));

            return diagnostics;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "CSharpDiagProvider: failed to analyze {File}", filePath);
            return [];
        }
        finally
        {
            _compileLock.Release();
        }
    }

    /// <summary>
    /// 移除一个文档（当文件被删除时）。
    /// </summary>
    public void RemoveDocument(string filePath)
    {
        if (_disposed) return;

        if (_documents.TryRemove(filePath, out var doc))
        {
            try
            {
                _workspace.CloseDocument(doc.Id);
            }
            catch
            {
                // 文档可能已被关闭
            }
        }
    }

    /// <summary>
    /// 获取指定文件的所有诊断（使用缓存结果）。
    /// </summary>
    public List<LspDiagnostic> GetDiagnostics(string filePath)
    {
        if (!_documents.ContainsKey(filePath))
            return [];

        return _lastDiagnostics
            .Where(d => d.Source == "Roslyn")
            .ToList();
    }

    /// <summary>
    /// 清除所有文档和诊断。
    /// </summary>
    public async Task ResetAsync()
    {
        if (_disposed) return;

        await _compileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _documents.Clear();
            _lastDiagnostics.Clear();
            HasErrors = false;
            _lastCompilation = null;

            // 重建工作区
            var solution = _workspace.CurrentSolution;
            var newProject = solution.AddProject("LTAI-Dynamic-Rebuilt", "LTAI-Dynamic-Rebuilt", LanguageNames.CSharp);
            newProject = newProject.WithCompilationOptions(
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                    .WithNullableContextOptions(NullableContextOptions.Enable));
            foreach (var assembly in GetDefaultAssemblies())
            {
                newProject = newProject.AddMetadataReference(assembly);
            }
            _workspace.TryApplyChanges(newProject.Solution);
        }
        finally
        {
            _compileLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _compileLock.Dispose();
        _workspace.Dispose();
    }

    // ═══════════════════════════════════════════════════════════
    //  辅助方法
    // ═══════════════════════════════════════════════════════════

    /// <summary>加载当前运行时的基础程序集引用。</summary>
    private static List<MetadataReference> GetDefaultAssemblies()
    {
        var refs = new List<MetadataReference>();

        // 从当前运行时加载核心程序集
        var assemblyNames = new[]
        {
            "System.Runtime",
            "System.Console",
            "System.Collections",
            "System.Linq",
            "System.Linq.Expressions",
            "System.Text.RegularExpressions",
            "System.Threading.Tasks",
            "System.Net.Http",
            "System.IO",
            "System.ComponentModel",
            "System.ObjectModel",
            "System.Memory",
            "System.Threading",
            "System.Private.CoreLib",
            "netstandard",
        };

        foreach (var name in assemblyNames)
        {
            try
            {
                var assembly = System.Reflection.Assembly.Load(name);
                var location = assembly.Location;
                if (!string.IsNullOrEmpty(location) && File.Exists(location))
                {
                    refs.Add(MetadataReference.CreateFromFile(location));
                }
            }
            catch
            {
                // 某些程序集可能无法加载——跳过
            }
        }

        return refs;
    }
}
