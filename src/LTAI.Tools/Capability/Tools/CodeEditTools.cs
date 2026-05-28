using System.ComponentModel;
using System.Text.Json;
using LTAI.Tools.CodeEngine;
using Microsoft.Extensions.Logging;

namespace LTAI.Tools.Tools;

[Description("Code editing operations: replace, insert, delete, validate syntax")]
public sealed class CodeEditTools
{
    private readonly CodeEditEngine _engine;
    private readonly ParserRegistry _parserRegistry;
    private readonly ILogger<CodeEditTools> _logger;

    public CodeEditTools(CodeEditEngine engine, ParserRegistry parserRegistry,
        ILogger<CodeEditTools>? logger = null)
    {
        _engine = engine;
        _parserRegistry = parserRegistry;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CodeEditTools>.Instance;
    }

    [Description("Replace code by exact text match (SEARCH/REPLACE). The SEARCH text must appear exactly once in the file. Safer than line-number editing. Adapted from DeepSeek-Reasonix.")]
    public async Task<string> EditSearchReplace(
        [Description("Absolute or relative file path")] string path,
        [Description("Exact text to find (must be unique in the file)")] string search,
        [Description("Text to substitute in place of search")] string replace,
        CancellationToken cancellationToken = default)
    {
        var result = await _engine.ApplyEditAsync(new EditOp
        {
            FilePath = path,
            Kind = EditOpKind.SearchReplace,
            SearchText = search,
            ReplaceText = replace,
        }).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            result.Success,
            result.WouldCompile,
            result.SnapshotId,
            errors = result.Errors,
            warnings = result.Warnings,
            diff = result.Diff?.UnifiedDiff,
            diffStats = result.Diff != null ? new
            {
                result.Diff.LinesAdded,
                result.Diff.LinesRemoved,
                result.Diff.LinesUnchanged,
            } : null,
        });
    }

    [Description("Replace a range of lines in a file. startLine/endLine are 1-based line numbers. Returns the unified diff.")]
    public async Task<string> EditReplaceRange(
        [Description("Absolute or relative file path")] string path,
        [Description("1-based start line number")] int startLine,
        [Description("1-based end line number (inclusive)")] int endLine,
        [Description("New code to insert in the range")] string newCode,
        CancellationToken cancellationToken = default)
    {
        var result = await _engine.ApplyEditAsync(new EditOp
        {
            FilePath = path,
            Kind = EditOpKind.ReplaceRange,
            StartLine = startLine,
            EndLine = endLine,
            NewCode = newCode,
        }).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            result.Success,
            result.WouldCompile,
            result.SnapshotId,
            errors = result.Errors,
            warnings = result.Warnings,
            diff = result.Diff?.UnifiedDiff,
            diffStats = result.Diff != null ? new
            {
                result.Diff.LinesAdded,
                result.Diff.LinesRemoved,
                result.Diff.LinesUnchanged,
            } : null,
        });
    }

    [Description("Replace a specific function body in a file. Uses AST to locate the function boundaries.")]
    public async Task<string> EditReplaceFunction(
        [Description("Absolute or relative file path")] string path,
        [Description("Name of the function to replace")] string functionName,
        [Description("New function code (full function declaration)")] string newCode,
        CancellationToken cancellationToken = default)
    {
        var result = await _engine.ApplyEditAsync(new EditOp
        {
            FilePath = path,
            Kind = EditOpKind.ReplaceFunction,
            FunctionName = functionName,
            NewCode = newCode,
        }).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            result.Success,
            result.WouldCompile,
            result.SnapshotId,
            errors = result.Errors,
            warnings = result.Warnings,
            functionName,
            diff = result.Diff?.UnifiedDiff,
        });
    }

    [Description("Insert new code after a specific line number. The new code is inserted on the next line.")]
    public async Task<string> EditInsertAfterLine(
        [Description("Absolute or relative file path")] string path,
        [Description("0-based or 1-based line number to insert after")] int line,
        [Description("Code to insert")] string code,
        CancellationToken cancellationToken = default)
    {
        var result = await _engine.ApplyEditAsync(new EditOp
        {
            FilePath = path,
            Kind = EditOpKind.InsertAfterLine,
            StartLine = line,
            NewCode = code,
        }).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            result.Success,
            result.WouldCompile,
            result.SnapshotId,
            errors = result.Errors,
            diff = result.Diff?.UnifiedDiff,
        });
    }

    [Description("Delete a range of lines from a file. Returns the deleted content for rollback.")]
    public async Task<string> EditDeleteRange(
        [Description("Absolute or relative file path")] string path,
        [Description("1-based start line number")] int startLine,
        [Description("1-based end line number (inclusive)")] int endLine,
        CancellationToken cancellationToken = default)
    {
        var result = await _engine.ApplyEditAsync(new EditOp
        {
            FilePath = path,
            Kind = EditOpKind.DeleteRange,
            StartLine = startLine,
            EndLine = endLine,
        }).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            result.Success,
            result.WouldCompile,
            result.SnapshotId,
            errors = result.Errors,
            diff = result.Diff?.UnifiedDiff,
        });
    }

    [Description("Validate syntax of a file using Roslyn (C#) or basic syntax check. Returns diagnostics.")]
    public async Task<string> EditValidateSyntax(
        [Description("Absolute or relative file path")] string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            return JsonSerializer.Serialize(new { error = $"File not found: {path}" });

        try
        {
            var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var language = LanguageRegistry.Detect(path);
            var parser = _parserRegistry.GetParser(language);

            if (parser != null && parser.SupportsDiagnostics)
            {
                var result = await parser.ParseAsync(content, path, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.Serialize(new
                {
                    path,
                    language = language.ToString(),
                    diagnostics = result.Diagnostics.Select(d => new
                    {
                        severity = d.Severity.ToString(),
                        line = d.Line,
                        column = d.Column,
                        message = d.Message,
                        code = d.Code,
                    }).ToList(),
                    errorCount = result.Diagnostics.Count(d => d.Severity == AstDiagnosticSeverity.Error),
                    warningCount = result.Diagnostics.Count(d => d.Severity == AstDiagnosticSeverity.Warning),
                });
            }

            var braceDiff = content.Count(c => c == '{') - content.Count(c => c == '}');
            var parenDiff = content.Count(c => c == '(') - content.Count(c => c == ')');

            return JsonSerializer.Serialize(new
            {
                path,
                language = language.ToString(),
                basicCheck = new
                {
                    braceBalance = braceDiff == 0 ? "ok" : $"off by {braceDiff}",
                    parenBalance = parenDiff == 0 ? "ok" : $"off by {parenDiff}",
                    totalLines = content.Split('\n').Length,
                    empty = string.IsNullOrWhiteSpace(content),
                },
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"Syntax validation failed: {ex.Message}" });
        }
    }

    [Description("Read a specific range of lines from a file. Much more efficient than reading the entire file for large files.")]
    public async Task<string> ReadRange(
        [Description("Absolute or relative file path")] string path,
        [Description("1-based start line number")] int startLine,
        [Description("Number of lines to read")] int count = 50,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            return JsonSerializer.Serialize(new { error = $"File not found: {path}" });

        var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        if (startLine < 1) startLine = 1;
        if (startLine > lines.Length)
            return JsonSerializer.Serialize(new { path, startLine, error = $"Start line {startLine} out of range (max {lines.Length})" });

        var endLine = Math.Min(startLine + count - 1, lines.Length);
        var selected = lines[(startLine - 1)..endLine];
        var content = string.Join('\n', selected);

        return JsonSerializer.Serialize(new
        {
            path,
            startLine,
            endLine,
            totalLines = lines.Length,
            linesRead = selected.Length,
            content,
            hasMoreAfter = endLine < lines.Length,
            hasMoreBefore = startLine > 1,
        });
    }

    [Description("Read a specific function from a file using AST. Returns only the function body with line numbers.")]
    public async Task<string> ReadFunction(
        [Description("Absolute or relative file path")] string path,
        [Description("Name of the function to read")] string functionName,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            return JsonSerializer.Serialize(new { error = $"File not found: {path}" });

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var language = LanguageRegistry.Detect(path);
        var parser = _parserRegistry.GetParser(language);

        if (parser == null)
            return JsonSerializer.Serialize(new { error = $"No parser available for {language}" });

        var result = await parser.ParseAsync(content, path, cancellationToken).ConfigureAwait(false);
        var function = result.Functions
            .FirstOrDefault(f => f.Name.Equals(functionName, StringComparison.OrdinalIgnoreCase));

        if (function == null)
        {
            var similar = result.Functions
                .Where(f => f.Name.Contains(functionName, StringComparison.OrdinalIgnoreCase))
                .Take(5)
                .Select(f => new { f.Name, f.Line, f.EndLine, f.ReturnType, parameters = string.Join(", ", f.Parameters) })
                .ToList();

            return JsonSerializer.Serialize(new
            {
                error = $"Function '{functionName}' not found",
                path,
                totalFunctions = result.Functions.Count,
                similarNames = similar,
            });
        }

        var lines = content.Split('\n');
        var functionLines = lines[(function.Line - 1)..function.EndLine];
        var functionCode = string.Join('\n', functionLines);

        return JsonSerializer.Serialize(new
        {
            function = new
            {
                function.Name,
                function.Line,
                function.EndLine,
                function.ReturnType,
                parameters = string.Join(", ", function.Parameters),
                function.Modifiers,
                function.ParentClass,
                function.Complexity,
                callCount = function.Calls.Count,
                callees = function.Calls.Select(c => c.Target).Distinct().Take(10).ToList(),
                hasDocumentation = !string.IsNullOrEmpty(function.Documentation),
            },
            path,
            code = functionCode.Length > 8000 ? functionCode[..8000] + $"\n... (truncated, {functionCode.Length} chars)" : functionCode,
            codeLength = functionCode.Length,
            tokenEstimate = functionCode.Length / 4,
        });
    }

    [Description("Read a specific class from a file using AST. Returns class definition with method/field signatures.")]
    public async Task<string> ReadClass(
        [Description("Absolute or relative file path")] string path,
        [Description("Name of the class to read")] string className,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            return JsonSerializer.Serialize(new { error = $"File not found: {path}" });

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var language = LanguageRegistry.Detect(path);
        var parser = _parserRegistry.GetParser(language);

        if (parser == null)
            return JsonSerializer.Serialize(new { error = $"No parser available for {language}" });

        var result = await parser.ParseAsync(content, path, cancellationToken).ConfigureAwait(false);
        var cls = result.Classes
            .FirstOrDefault(c => c.Name.Equals(className, StringComparison.OrdinalIgnoreCase));

        if (cls == null)
        {
            var similar = result.Classes
                .Select(c => new { c.Name, c.Line, c.Kind, c.MethodCount })
                .Take(10)
                .ToList();

            return JsonSerializer.Serialize(new
            {
                error = $"Class '{className}' not found",
                path,
                totalClasses = result.Classes.Count,
                availableClasses = similar,
            });
        }

        var lines = content.Split('\n');
        var classCode = cls.EndLine > cls.Line
            ? string.Join('\n', lines[(cls.Line - 1)..cls.EndLine])
            : lines[cls.Line - 1];

        return JsonSerializer.Serialize(new
        {
            cls = new
            {
                cls.Name,
                cls.Kind,
                cls.Line,
                cls.EndLine,
                cls.Modifiers,
                cls.BaseTypes,
                cls.MethodCount,
                cls.PropertyCount,
                cls.FieldCount,
                methods = cls.Methods.Take(20).ToList(),
                properties = cls.Properties.Take(20).ToList(),
                fields = cls.Fields.Take(20).ToList(),
            },
            path,
            code = classCode.Length > 8000 ? classCode[..8000] + $"\n... (truncated, {classCode.Length} chars)" : classCode,
            codeLength = classCode.Length,
            tokenEstimate = classCode.Length / 4,
        });
    }

    [Description("List all function signatures and class summaries in a file. Lightweight overview without full source.")]
    public async Task<string> ReadStructure(
        [Description("Absolute or relative file path")] string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            return JsonSerializer.Serialize(new { error = $"File not found: {path}" });

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var language = LanguageRegistry.Detect(path);
        var parser = _parserRegistry.GetParser(language);

        if (parser == null)
            return JsonSerializer.Serialize(new { error = $"No parser available for {language}" });

        var result = await parser.ParseAsync(content, path, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            path,
            language = language.ToString(),
            totalLines = result.TotalLines,
            codeLines = result.CodeLines,
            complexity = result.CyclomaticComplexity,
            classes = result.Classes.Select(c => new
            {
                c.Name, c.Kind, c.Line, c.EndLine, c.MethodCount, c.PropertyCount,
                baseTypes = c.BaseTypes,
            }).ToList(),
            functions = result.Functions.Select(f => new
            {
                f.Name, f.Line, f.EndLine, f.ReturnType,
                parameters = string.Join(", ", f.Parameters),
                f.Modifiers, f.ParentClass, f.Complexity,
                calls = f.Calls.Select(c => c.Target).Distinct().Count(),
            }).ToList(),
            imports = result.Imports.Select(i => i.Module).ToList(),
            diagnostics = result.Diagnostics
                .Where(d => d.Severity == AstDiagnosticSeverity.Error || d.Severity == AstDiagnosticSeverity.Warning)
                .Take(20)
                .Select(d => new { severity = d.Severity.ToString(), d.Line, d.Message, d.Code }),
        });
    }

    [Description("Generate a unified diff between original and current file. Shows what changed in prior edits.")]
    public string EditDiff(
        [Description("Absolute or relative file path")] string path,
        [Description("Snapshot ID to diff against (from previous edit result)")] string? snapshotId = null)
    {
        if (!File.Exists(path))
            return JsonSerializer.Serialize(new { error = $"File not found: {path}" });

        try
        {
            var currentContent = File.ReadAllText(path);
            string? originalContent = null;

            if (!string.IsNullOrEmpty(snapshotId))
            {
                var snapshotDir = Path.Combine(Environment.GetEnvironmentVariable("LTAI_WORKSPACE") ?? Directory.GetCurrentDirectory(), ".livingtree", "edit_snapshots");
                var snapshotFile = Path.Combine(snapshotDir, $"{snapshotId}_{Path.GetFileName(path)}");
                if (File.Exists(snapshotFile))
                    originalContent = File.ReadAllText(snapshotFile);
            }

            if (originalContent == null)
            {
                return JsonSerializer.Serialize(new { path, message = "No snapshot to diff against. Use snapshotId from a previous edit result." });
            }

            var diff = _engine.GenerateDiff(originalContent, "");
            return JsonSerializer.Serialize(new
            {
                path,
                snapshotId,
                diff = diff.UnifiedDiff,
                linesAdded = diff.LinesAdded,
                linesRemoved = diff.LinesRemoved,
                linesUnchanged = diff.LinesUnchanged,
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"Diff failed: {ex.Message}" });
        }
    }
}
