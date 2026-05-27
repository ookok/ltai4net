using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using LTAI.Core.Governors;
using LTAI.Tools.CodeGraph;
using LTAI.Tools.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Tools.CodeEngine;

public enum EditOpKind { ReplaceRange, InsertAfterLine, DeleteRange, ReplaceFunction }

public sealed record EditOp
{
    public EditOpKind Kind { get; init; }
    public string FilePath { get; init; } = "";
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public string? NewCode { get; init; }
    public string? FunctionName { get; init; }
}

public sealed record DiffResult
{
    public string FilePath { get; init; } = "";
    public string UnifiedDiff { get; init; } = "";
    public int LinesAdded { get; init; }
    public int LinesRemoved { get; init; }
    public int LinesUnchanged { get; init; }
}

public sealed record EditResult
{
    public bool Success { get; init; }
    public string SnapshotId { get; init; } = "";
    public DiffResult? Diff { get; init; }
    public List<string> Errors { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
    public bool WouldCompile { get; init; }
    public string OriginalHash { get; init; } = "";
    public string NewHash { get; init; } = "";
}

public sealed class CodeEditEngine
{
    private readonly string _workspace;
    private readonly string _snapshotDir;
    private readonly ICodeParser? _parser;
    private readonly ILogger<CodeEditEngine> _logger;
    private readonly Lock _snapshotLock = new();
    private IMicroKernel? _kernel;

    public CodeEditEngine(string? workspace = null, ICodeParser? parser = null,
        IMicroKernel? kernel = null, ILogger<CodeEditEngine>? logger = null)
    {
        _workspace = workspace ?? Directory.GetCurrentDirectory();
        _snapshotDir = Path.Combine(OptionService.Get("paths.livingtree") ?? Path.Combine(_workspace, ".livingtree"), "edit_snapshots");
        Directory.CreateDirectory(_snapshotDir);
        _parser = parser;
        _kernel = kernel;
        _logger = logger ?? NullLogger<CodeEditEngine>.Instance;
    }

    public async Task<EditResult> ApplyEditAsync(EditOp op)
    {
        var snapshot = CreateSnapshot(op.FilePath);
        try
        {
            var result = op.Kind switch
            {
                EditOpKind.ReplaceRange => ApplyReplaceRange(op),
                EditOpKind.InsertAfterLine => ApplyInsertAfterLine(op),
                EditOpKind.DeleteRange => ApplyDeleteRange(op),
                EditOpKind.ReplaceFunction => await ApplyReplaceFunctionAsync(op),
                _ => new EditResult { Success = false, Errors = new() { $"Unknown operation: {op.Kind}" } },
            };

            if (!result.Success)
            {
                RestoreSnapshot(snapshot, op.FilePath);
                return result;
            }

            var diff = GenerateDiff(snapshot.Content, result.NewHash);
            var diagnostics = await ValidateSyntaxAsync(op.FilePath).ConfigureAwait(false);

            return result with
            {
                Diff = diff,
                Warnings = diagnostics.Warnings,
                WouldCompile = diagnostics.Errors.Count == 0,
                Errors = result.Errors.Count > 0 ? result.Errors : diagnostics.Errors,
            };
        }
        catch (Exception ex)
        {
            RestoreSnapshot(snapshot, op.FilePath);
            _logger.LogError(ex, "Edit failed for {Path} op {Op}", op.FilePath, op.Kind);
            return new EditResult
            {
                Success = false,
                SnapshotId = snapshot.Id,
                Errors = new() { ex.Message },
            };
        }
    }

    public async Task<EditResult> ApplyBatchAsync(List<EditOp> ops)
    {
        var snapshots = new List<SnapshotData>();
        foreach (var op in ops)
            snapshots.Add(CreateSnapshot(op.FilePath));

        var results = new List<EditResult>();
        var allSuccess = true;
        foreach (var op in ops)
        {
            var r = await ApplyEditAsync(op).ConfigureAwait(false);
            r = r with { SnapshotId = snapshots.First(s => s.FilePath == op.FilePath).Id };
            results.Add(r);
            if (!r.Success) allSuccess = false;
        }

        if (!allSuccess)
        {
            foreach (var s in snapshots)
                RestoreSnapshot(s, s.FilePath);
        }

        return new EditResult
        {
            Success = allSuccess,
            SnapshotId = string.Join(";", snapshots.Select(s => s.Id)),
            Errors = results.SelectMany(r => r.Errors).ToList(),
            Warnings = results.SelectMany(r => r.Warnings).ToList(),
        };
    }

    public async Task<EditOp[]> SuggestEditsAsync(string filePath, DescribeSymbol description)
    {
        if (_parser == null || !File.Exists(filePath))
            return Array.Empty<EditOp>();

        var content = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
        var result = await _parser.ParseAsync(content, filePath).ConfigureAwait(false);

        int line = 0, endLine = 0;
        string? functionName = null;

        if (!string.IsNullOrEmpty(description.FunctionName))
        {
            var func = result.Functions.FirstOrDefault(f =>
                f.Name.Equals(description.FunctionName, StringComparison.OrdinalIgnoreCase));
            if (func != null) { line = func.Line; endLine = func.EndLine; functionName = func.Name; }
        }
        else if (!string.IsNullOrEmpty(description.ClassName))
        {
            var cls = result.Classes.FirstOrDefault(c =>
                c.Name.Equals(description.ClassName, StringComparison.OrdinalIgnoreCase));
            if (cls != null) { line = cls.Line; endLine = cls.EndLine; }
        }
        else if (description.Line > 0)
        {
            var func = result.Functions
                .Where(f => f.Line <= description.Line && f.EndLine >= description.Line)
                .MinBy(f => f.EndLine - f.Line);
            if (func != null) { line = func.Line; endLine = func.EndLine; functionName = func.Name; }
        }

        if (line == 0) return Array.Empty<EditOp>();

        return new[]
        {
            new EditOp
            {
                FilePath = filePath,
                StartLine = line,
                EndLine = endLine,
                Kind = EditOpKind.ReplaceRange,
                FunctionName = functionName,
            },
        };
    }

    private sealed record SnapshotData(string Id, string FilePath, string Content);

    private SnapshotData CreateSnapshot(string filePath)
    {
        if (!File.Exists(filePath))
            return new SnapshotData(Guid.NewGuid().ToString("N")[..12], filePath, "");

        var content = File.ReadAllText(filePath);
        var id = Guid.NewGuid().ToString("N")[..12];
        var snapshotFile = Path.Combine(_snapshotDir, $"{id}_{Path.GetFileName(filePath)}");

        lock (_snapshotLock)
            File.WriteAllText(snapshotFile, content);

        return new SnapshotData(id, filePath, content);
    }

    private void RestoreSnapshot(SnapshotData snapshot, string filePath)
    {
        if (!string.IsNullOrEmpty(snapshot.Content))
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, snapshot.Content);
        }

        var snapshotFile = Path.Combine(_snapshotDir, $"{snapshot.Id}_{Path.GetFileName(filePath)}");
        if (File.Exists(snapshotFile))
        {
            try { File.Delete(snapshotFile); }
            catch { /* intentional: cleanup may fail */ }
        }
    }

    private EditResult ApplyReplaceRange(EditOp op)
    {
        if (!File.Exists(op.FilePath))
            return new() { Success = false, Errors = new() { $"File not found: {op.FilePath}" } };

        var content = File.ReadAllText(op.FilePath);
        var originalHash = HashContent(content);
        var lines = content.Split('\n');

        if (op.StartLine < 1) op = op with { StartLine = 1 };
        if (op.EndLine > lines.Length) op = op with { EndLine = lines.Length };
        if (op.StartLine > lines.Length)
            return new() { Success = false, Errors = new() { $"Line {op.StartLine} out of range (max {lines.Length})" }, OriginalHash = originalHash };

        var newCode = op.NewCode ?? "";
        var newLines = newCode.Replace("\r\n", "\n").Split('\n');

        var before = op.StartLine > 1 ? lines.Take(op.StartLine - 1) : Array.Empty<string>();
        var after = op.EndLine < lines.Length ? lines.Skip(op.EndLine) : Array.Empty<string>();
        var result = before.Concat(newLines).Concat(after);
        var newContent = string.Join('\n', result);
        var newHash = HashContent(newContent);

        File.WriteAllText(op.FilePath, newContent);

        return new EditResult
        {
            Success = true,
            SnapshotId = "",
            OriginalHash = originalHash,
            NewHash = newHash,
        };
    }

    private EditResult ApplyInsertAfterLine(EditOp op)
    {
        if (!File.Exists(op.FilePath))
            return new() { Success = false, Errors = new() { $"File not found: {op.FilePath}" } };

        var content = File.ReadAllText(op.FilePath);
        var originalHash = HashContent(content);
        var lines = content.Split('\n').ToList();

        var insertLine = Math.Clamp(op.StartLine, 0, lines.Count);
        var newCode = op.NewCode ?? "";
        var newCodeLines = newCode.Replace("\r\n", "\n").Split('\n');

        lines.InsertRange(insertLine, newCodeLines);
        var newContent = string.Join('\n', lines);
        var newHash = HashContent(newContent);

        File.WriteAllText(op.FilePath, newContent);

        return new EditResult { Success = true, SnapshotId = "", OriginalHash = originalHash, NewHash = newHash };
    }

    private EditResult ApplyDeleteRange(EditOp op)
    {
        if (!File.Exists(op.FilePath))
            return new() { Success = false, Errors = new() { $"File not found: {op.FilePath}" } };

        var content = File.ReadAllText(op.FilePath);
        var originalHash = HashContent(content);
        var lines = content.Split('\n');

        if (op.StartLine < 1) op = op with { StartLine = 1 };
        if (op.EndLine > lines.Length) op = op with { EndLine = lines.Length };

        var before = op.StartLine > 1 ? lines.Take(op.StartLine - 1) : Array.Empty<string>();
        var after = op.EndLine < lines.Length ? lines.Skip(op.EndLine) : Array.Empty<string>();
        var result = before.Concat(after);
        var newContent = string.Join('\n', result);
        var newHash = HashContent(newContent);

        File.WriteAllText(op.FilePath, newContent);

        return new EditResult { Success = true, SnapshotId = "", OriginalHash = originalHash, NewHash = newHash };
    }

    private async Task<EditResult> ApplyReplaceFunctionAsync(EditOp op)
    {
        if (_parser == null || !File.Exists(op.FilePath))
            return new() { Success = false, Errors = new() { "Parser not available or file not found" } };

        var content = await File.ReadAllTextAsync(op.FilePath).ConfigureAwait(false);
        var parseResult = await _parser.ParseAsync(content, op.FilePath).ConfigureAwait(false);

        var function = parseResult.Functions
            .FirstOrDefault(f => f.Name.Equals(op.FunctionName ?? "", StringComparison.OrdinalIgnoreCase));

        if (function == null)
            return new() { Success = false, Errors = new() { $"Function '{op.FunctionName}' not found in {op.FilePath}" } };

        return ApplyReplaceRange(new EditOp
        {
            FilePath = op.FilePath,
            StartLine = function.Line,
            EndLine = function.EndLine,
            Kind = EditOpKind.ReplaceRange,
            NewCode = op.NewCode,
        });
    }

    private static string HashContent(string content)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash)[..16];
    }

    public DiffResult GenerateDiff(string oldContent, string newHash)
    {
        var filePath = Path.GetTempFileName();
        var tempOld = Path.GetTempFileName();

        try
        {
            File.WriteAllText(tempOld, oldContent);

            if (_kernel != null)
            {
                var result = _kernel.GitOpAsync("diff", $"--no-index --unified=3 {tempOld} {filePath}", CancellationToken.None)
                    .GetAwaiter().GetResult();
                if (result.Success && !string.IsNullOrEmpty(result.Data))
                {
                    var diffOutput = result.Data;
                    var added = Regex.Matches(diffOutput, @"^\+[^+]", RegexOptions.Multiline).Count;
                    var removed = Regex.Matches(diffOutput, @"^-[^-]", RegexOptions.Multiline).Count;
                    var unchanged = Regex.Matches(diffOutput, @"^ [^ ]|^$", RegexOptions.Multiline).Count;
                    return new DiffResult { FilePath = filePath, UnifiedDiff = diffOutput, LinesAdded = added, LinesRemoved = removed, LinesUnchanged = unchanged };
                }
            }

            var psi = new ProcessStartInfo("git", $"diff --no-index --unified=3 {tempOld} {filePath}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                proc.WaitForExit(5000);
                var diffOutput = proc.StandardOutput.ReadToEnd();
                if (!string.IsNullOrWhiteSpace(diffOutput))
                {
                    var added = Regex.Matches(diffOutput, @"^\+[^+]", RegexOptions.Multiline).Count;
                    var removed = Regex.Matches(diffOutput, @"^-[^-]", RegexOptions.Multiline).Count;
                    var unchanged = Regex.Matches(diffOutput, @"^ [^ ]|^$", RegexOptions.Multiline).Count;

                    return new DiffResult
                    {
                        FilePath = filePath,
                        UnifiedDiff = diffOutput,
                        LinesAdded = added,
                        LinesRemoved = removed,
                        LinesUnchanged = unchanged,
                    };
                }
            }
        }
        catch { /* intentional: cleanup may fail */ }
        finally
        {
            try { File.Delete(tempOld); } catch { /* intentional: cleanup may fail */ }
        }

        return ComputeSimpleDiff(oldContent, newHash);
    }

    private DiffResult ComputeSimpleDiff(string oldContent, string newHash)
    {
        var oldLines = oldContent.Split('\n');
        var added = 0;
        var removed = 0;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("--- a/file");
        sb.AppendLine("+++ b/file");

        var maxLen = Math.Max(oldLines.Length, oldLines.Length);
        for (var i = 0; i < maxLen && i < oldLines.Length; i++)
        {
            sb.AppendLine($" {oldLines[i]}");
        }

        return new DiffResult
        {
            FilePath = "",
            UnifiedDiff = sb.ToString(),
            LinesAdded = added,
            LinesRemoved = removed,
            LinesUnchanged = oldLines.Length,
        };
    }

    private async Task<(List<string> Errors, List<string> Warnings)> ValidateSyntaxAsync(string filePath)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        try
        {
            if (_parser != null && _parser.SupportsDiagnostics)
            {
                var content = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
                var result = await _parser.ParseAsync(content, filePath).ConfigureAwait(false);
                foreach (var diag in result.Diagnostics)
                {
                    var msg = $"{filePath}:{diag.Line}: {diag.Message} [{diag.Code}]";
                    if (diag.Severity == AstDiagnosticSeverity.Error)
                        errors.Add(msg);
                    else if (diag.Severity == AstDiagnosticSeverity.Warning)
                        warnings.Add(msg);
                }
            }
            else
            {
                var content = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
                var unclosedBraces = content.Count(c => c == '{') - content.Count(c => c == '}');
                if (unclosedBraces != 0)
                    errors.Add($"Brace mismatch: {unclosedBraces} unclosed braces in {filePath}");

                var unclosedParens = content.Count(c => c == '(') - content.Count(c => c == ')');
                if (unclosedParens != 0)
                    warnings.Add($"Parenthesis mismatch: {unclosedParens} in {filePath}");
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Syntax validation failed: {ex.Message}");
        }

        return (errors, warnings);
    }

    public void CleanupSnapshots(int keepLast = 50)
    {
        try
        {
            var files = Directory.GetFiles(_snapshotDir)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(keepLast)
                .ToList();

            foreach (var file in files)
            {
                try { file.Delete(); } catch { /* intentional: cleanup may fail */ }
            }
        }
        catch { /* intentional: cleanup may fail */ }
    }

    private static object? FindSymbol(CodeParseResult parseResult, DescribeSymbol desc)
    {
        if (!string.IsNullOrEmpty(desc.FunctionName))
            return parseResult.Functions.FirstOrDefault(f =>
                f.Name.Equals(desc.FunctionName, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(desc.ClassName))
            return parseResult.Classes.FirstOrDefault(c =>
                c.Name.Equals(desc.ClassName, StringComparison.OrdinalIgnoreCase));

        if (desc.Line > 0)
        {
            return parseResult.Functions
                .Where(f => f.Line <= desc.Line && f.EndLine >= desc.Line)
                .MinBy(f => f.EndLine - f.Line);
        }

        return null;
    }
}

public sealed class DescribeSymbol
{
    public string? FunctionName { get; set; }
    public string? ClassName { get; set; }
    public int Line { get; set; }
    public string? FilePath { get; set; }
}
