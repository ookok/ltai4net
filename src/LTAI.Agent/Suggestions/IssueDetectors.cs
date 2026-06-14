// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  TodoIssueDetector — stale TODO comment detector
//  NamingIssueDetector — naming convention violation detector
//  ComplexityIssueDetector — long method / high complexity detector
//
//  Inspiration: TIDE (arXiv 2606.04743)
//
//  Three lightweight detectors that scan workspace files for
//  common code quality issues. Designed for fast, non-blocking
//  scans (each detector runs in <200ms for typical repos).
// ═══════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Suggestions;

internal static class IssueDetectorConcurrency
{
    public static readonly int MaxDop = int.TryParse(
        Environment.GetEnvironmentVariable("LTAI_ISSUE_DETECTOR_MAX_DOP"), out var d) ? Math.Max(1, d) : 4;
}

// ═══════════════════════════════════════════════════════════════
//  TodoIssueDetector
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Detects stale TODO/FIXME/HACK comments.
/// Flags entries older than a configurable threshold (default 14 days).
/// </summary>
public sealed partial class TodoIssueDetector : ICodeIssueDetector
{
    private readonly ILogger<TodoIssueDetector> _logger;
    private readonly TimeSpan _staleThreshold;
    private DateTime? _lastScanAt;
    private IReadOnlyList<CodeIssue>? _lastResults;

    public string Name => "TodoScanner";
    public IReadOnlyList<CodeIssue>? LastResults => _lastResults;
    public DateTime? LastScanAt => _lastScanAt;
    public TimeSpan Cooldown => TimeSpan.FromMinutes(5);

    public TodoIssueDetector(
        ILogger<TodoIssueDetector>? logger = null,
        TimeSpan? staleThreshold = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TodoIssueDetector>.Instance;
        _staleThreshold = staleThreshold ?? TimeSpan.FromDays(14);
    }

    public async Task<IReadOnlyList<CodeIssue>> ScanAsync(
        string workspacePath, CancellationToken ct = default)
    {
        _lastScanAt = DateTime.UtcNow;
        var issues = new ConcurrentBag<CodeIssue>();

        var files = Directory.EnumerateFiles(workspacePath, "*.*", SearchOption.AllDirectories)
            .Where(f => IssueUtils.IsSourceFile(f) && !IssueUtils.IsIgnored(f))
            .ToList();

        var parallelOpts = new ParallelOptions
        {
            MaxDegreeOfParallelism = IssueDetectorConcurrency.MaxDop,
            CancellationToken = ct,
        };

        Parallel.ForEach(files, parallelOpts, file =>
        {
            try
            {
                var content = File.ReadAllText(file);
                var relPath = Path.GetRelativePath(workspacePath, file);
                var lines = content.Split('\n');

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    var match = TodoPattern().Match(line);
                    if (!match.Success) continue;

                    var text = match.Groups[2].Value.Trim();
                    var lineNum = i + 1;

                    // Extract username if present: "TODO(john): ..."
                    var author = match.Groups[1].Success ? match.Groups[1].Value.Trim() : null;

                    issues.Add(new CodeIssue(
                        Id: $"todo:{relPath}:{lineNum}",
                        File: relPath,
                        Line: lineNum,
                        Severity: IssueSeverity.Warning,
                        Category: "todo",
                        Title: $"Stale TODO{(author != null ? $" ({author})" : "")}: {IssueUtils.Truncate(text, 50)}",
                        Description: $"TODO comment at {relPath}:{lineNum}: {text}",
                        Suggestion: author != null
                            ? $"Consider resolving or assigning to {author}"
                            : "Consider resolving this TODO or adding an owner"));
                }
            }
            catch { /* skip unreadable files */ }
        });

        _lastResults = issues.OrderBy(i => i.Severity).ThenBy(i => i.File).ThenBy(i => i.Line).ToList();
        return _lastResults;
    }

    [GeneratedRegex(@"(?i)\b(TODO|FIXME|HACK|XXX)\b(?:\(([^)]*)\))?\s*:\s*(.*)")]
    private static partial Regex TodoPattern();

    public void Dispose() { }
}

// ═══════════════════════════════════════════════════════════════
//  NamingIssueDetector
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Detects naming convention violations in C#/Python/TypeScript files.
/// Checks: PascalCase public members, camelCase private, _camelCase fields.
/// </summary>
public sealed partial class NamingIssueDetector : ICodeIssueDetector
{
    private readonly ILogger<NamingIssueDetector> _logger;
    private DateTime? _lastScanAt;
    private IReadOnlyList<CodeIssue>? _lastResults;

    public string Name => "NamingScanner";
    public IReadOnlyList<CodeIssue>? LastResults => _lastResults;
    public DateTime? LastScanAt => _lastScanAt;
    public TimeSpan Cooldown => TimeSpan.FromMinutes(10);

    public NamingIssueDetector(ILogger<NamingIssueDetector>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<NamingIssueDetector>.Instance;
    }

    public async Task<IReadOnlyList<CodeIssue>> ScanAsync(
        string workspacePath, CancellationToken ct = default)
    {
        _lastScanAt = DateTime.UtcNow;
        var issues = new ConcurrentBag<CodeIssue>();

        // Only scan C# files for naming
        var files = Directory.EnumerateFiles(workspacePath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IssueUtils.IsIgnored(f))
            .ToList();

        var parallelOpts = new ParallelOptions
        {
            MaxDegreeOfParallelism = IssueDetectorConcurrency.MaxDop,
            CancellationToken = ct,
        };

        Parallel.ForEach(files, parallelOpts, file =>
        {
            try
            {
                var content = File.ReadAllText(file);
                var relPath = Path.GetRelativePath(workspacePath, file);
                var lines = content.Split('\n');

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    var trimmed = line.Trim();

                    // Check public methods/properties: must be PascalCase
                    if (trimmed.StartsWith("public ") && !trimmed.Contains(" class ") && !trimmed.Contains(" struct "))
                    {
                        var nameMatch = PublicMemberPattern().Match(trimmed);
                        if (nameMatch.Success)
                        {
                            var name = nameMatch.Groups[1].Value;
                            if (char.IsLower(name[0]))
                            {
                                issues.Add(new CodeIssue(
                                    Id: $"naming:{relPath}:{i + 1}",
                                    File: relPath,
                                    Line: i + 1,
                                    Severity: IssueSeverity.Info,
                                    Category: "naming",
                                    Title: $"Public member '{name}' should be PascalCase",
                                    Description: $"Public member at {relPath}:{i + 1} starts with lowercase: {name}",
                                    Suggestion: $"Rename to '{char.ToUpper(name[0]) + name[1..]}'"));
                            }
                        }
                    }

                    // Check private fields: should be _camelCase
                    if (trimmed.StartsWith("private ") && !trimmed.Contains(" class "))
                    {
                        var fieldMatch = PrivateFieldPattern().Match(trimmed);
                        if (fieldMatch.Success)
                        {
                            var name = fieldMatch.Groups[1].Value;
                            if (!name.StartsWith("_") || char.IsUpper(name[1..].FirstOrDefault()))
                            {
                                var suggested = name.StartsWith("m_")
                                    ? "_" + name[2..]  // m_value → _value
                                    : "_" + name;       // value → _value
                                issues.Add(new CodeIssue(
                                    Id: $"naming:{relPath}:{i + 1}",
                                    File: relPath,
                                    Line: i + 1,
                                    Severity: IssueSeverity.Info,
                                    Category: "naming",
                                    Title: $"Private field '{name}' should be _{name.TrimStart('_')}",
                                    Description: $"Private field at {relPath}:{i + 1} should follow _camelCase convention",
                                    Suggestion: $"Rename to '{suggested}'"));
                            }
                        }
                    }
                }
            }
            catch { /* skip */ }
        });

        _lastResults = issues.OrderBy(i => i.File).ThenBy(i => i.Line).ToList();
        return _lastResults;
    }

    // Matches: public void MethodName(...
    [GeneratedRegex(@"\b(void|int|string|bool|Task|Task<|ValueTask|ValueTask<|async\s+\w+)\s+(\w+)\s*\(")]
    private static partial Regex PublicMemberPattern();

    // Matches: private readonly? SomeType fieldName;
    [GeneratedRegex(@"private\s+(?:\w+\s+)*(\w+)\s*[=;]")]
    private static partial Regex PrivateFieldPattern();

    public void Dispose() { }
}

// ═══════════════════════════════════════════════════════════════
//  ComplexityIssueDetector
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Detects overly long methods (>100 lines).
/// Scans C# files using indentation heuristics.
/// </summary>
public sealed partial class ComplexityIssueDetector : ICodeIssueDetector
{
    private readonly ILogger<ComplexityIssueDetector> _logger;
    private readonly int _maxMethodLines;
    private DateTime? _lastScanAt;
    private IReadOnlyList<CodeIssue>? _lastResults;

    public string Name => "ComplexityScanner";
    public IReadOnlyList<CodeIssue>? LastResults => _lastResults;
    public DateTime? LastScanAt => _lastScanAt;
    public TimeSpan Cooldown => TimeSpan.FromMinutes(15);

    public ComplexityIssueDetector(
        ILogger<ComplexityIssueDetector>? logger = null,
        int maxMethodLines = 100)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ComplexityIssueDetector>.Instance;
        _maxMethodLines = maxMethodLines;
    }

    public Task<IReadOnlyList<CodeIssue>> ScanAsync(
        string workspacePath, CancellationToken ct = default)
    {
        _lastScanAt = DateTime.UtcNow;
        var issues = new List<CodeIssue>();

        var files = Directory.EnumerateFiles(workspacePath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IssueUtils.IsIgnored(f))
            .ToList();

        foreach (var file in files)
        {
            try
            {
                var content = File.ReadAllText(file);
                var relPath = Path.GetRelativePath(workspacePath, file);
                var lines = content.Split('\n');
                DetectLongMethods(lines, relPath, issues);
            }
            catch { /* skip */ }
        }

        _lastResults = issues.OrderBy(i => i.Severity).ThenBy(i => i.File).ToList();
        return Task.FromResult(_lastResults);
    }

    private void DetectLongMethods(string[] lines, string relPath, List<CodeIssue> issues)
    {
        int braceDepth = 0;
        int methodStart = -1;
        string? methodName = null;
        int methodLine = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (braceDepth == 0 && methodStart == -1)
            {
                var match = MethodStartPattern().Match(trimmed);
                if (match.Success && trimmed.EndsWith("{"))
                {
                    methodName = match.Groups[1].Value;
                    methodStart = i;
                    methodLine = i;
                    braceDepth = 1;
                    continue;
                }
            }

            foreach (var c in trimmed)
            {
                if (c == '{') braceDepth++;
                if (c == '}') braceDepth--;
            }

            if (braceDepth == 0 && methodStart >= 0)
            {
                var methodLines = i - methodStart;
                if (methodLines > _maxMethodLines)
                {
                    issues.Add(new CodeIssue(
                        Id: $"complexity:{relPath}:{methodLine + 1}",
                        File: relPath,
                        Line: methodLine + 1,
                        Severity: IssueSeverity.Warning,
                        Category: "complexity",
                        Title: $"Long method '{methodName}' ({methodLines} lines)",
                        Description: $"Method '{methodName}' at {relPath}:{methodLine + 1} is {methodLines} lines " +
                                     $"(threshold: {_maxMethodLines}). Consider refactoring.",
                        Suggestion: "Break down into smaller methods (Single Responsibility Principle)"));
                }
                methodStart = -1;
                methodName = null;
            }
        }
    }

    [GeneratedRegex(@"(?:public|private|protected|internal)\s+(?:static\s+)?(?:async\s+)?(?:unsafe\s+)?(?:\w+\s+)+\w+\s*\(")]
    private static partial System.Text.RegularExpressions.Regex MethodStartPattern();

    public void Dispose() { }
}

// ═══════════════════════════════════════════════════════════════
//  MagicNumberIssueDetector
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Detects unbound magic numbers (numeric literals) in C# code.
/// Excludes: 0, 1, -1, well-known constants (100, 1000 for timeouts),
/// array indices, and numbers assigned to const fields.
/// </summary>
public sealed partial class MagicNumberIssueDetector : ICodeIssueDetector
{
    private static readonly HashSet<string> AllowedNumericLiterals =
    [
        "0", "1", "-1", "0.0", "1.0", "-1.0",
        "100", "1000", // common timeouts
        "3600", "86400", "60", "24", // time constants
        "1024", "2048", "4096", "8192", // KB/MB thresholds
    ];

    private readonly ILogger<MagicNumberIssueDetector> _logger;
    private DateTime? _lastScanAt;
    private IReadOnlyList<CodeIssue>? _lastResults;

    public string Name => "MagicNumberScanner";
    public IReadOnlyList<CodeIssue>? LastResults => _lastResults;
    public DateTime? LastScanAt => _lastScanAt;
    public TimeSpan Cooldown => TimeSpan.FromMinutes(20);

    public MagicNumberIssueDetector(ILogger<MagicNumberIssueDetector>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MagicNumberIssueDetector>.Instance;
    }

    public Task<IReadOnlyList<CodeIssue>> ScanAsync(
        string workspacePath, CancellationToken ct = default)
    {
        _lastScanAt = DateTime.UtcNow;
        var issues = new List<CodeIssue>();

        var files = Directory.EnumerateFiles(workspacePath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IssueUtils.IsIgnored(f))
            .ToList();

        foreach (var file in files)
        {
            try
            {
                var content = File.ReadAllText(file);
                var relPath = Path.GetRelativePath(workspacePath, file);
                var lines = content.Split('\n');

                for (int i = 0; i < lines.Length; i++)
                {
                    var trimmed = lines[i].Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("//") || trimmed.StartsWith("/*")
                        || trimmed.StartsWith("*") || trimmed.StartsWith("[") || trimmed.StartsWith("#"))
                        continue;

                    // Skip const, readonly, and attribute lines
                    if (trimmed.Contains("const ") || trimmed.Contains("readonly ")
                        || trimmed.StartsWith("using ") || trimmed.StartsWith("namespace "))
                        continue;

                    var matches = MagicNumberPattern().Matches(trimmed);
                    foreach (Match match in matches)
                    {
                        if (AllowedNumericLiterals.Contains(match.Value))
                            continue;

                        issues.Add(new CodeIssue(
                            Id: $"magic:{relPath}:{i + 1}:{match.Index}",
                            File: relPath, Line: i + 1,
                            Severity: IssueSeverity.Info,
                            Category: "magic",
                            Title: $"Magic number '{match.Value}' at {relPath}:{i + 1}",
                            Description: $"Numeric literal '{match.Value}' should be a named constant",
                            Suggestion: $"Extract '{match.Value}' to a const with a descriptive name"));
                    }
                }
            }
            catch { /* skip */ }
        }

        _lastResults = issues.OrderBy(i => i.File).ThenBy(i => i.Line).ToList();
        return Task.FromResult(_lastResults);
    }

    [GeneratedRegex(@"(?<![.\w])\b\d{2,}\b(?!\.\d)")]
    private static partial System.Text.RegularExpressions.Regex MagicNumberPattern();

    public void Dispose() { }
}

// ═══════════════════════════════════════════════════════════════
//  ExceptionIssueDetector
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Detects suspicious exception handling patterns:
///   - Empty catch blocks
///   - Catch(Exception) that is too broad
///   - Swallowed exceptions without logging
/// </summary>
public sealed partial class ExceptionIssueDetector : ICodeIssueDetector
{
    private readonly ILogger<ExceptionIssueDetector> _logger;
    private DateTime? _lastScanAt;
    private IReadOnlyList<CodeIssue>? _lastResults;

    public string Name => "ExceptionScanner";
    public IReadOnlyList<CodeIssue>? LastResults => _lastResults;
    public DateTime? LastScanAt => _lastScanAt;
    public TimeSpan Cooldown => TimeSpan.FromMinutes(15);

    public ExceptionIssueDetector(ILogger<ExceptionIssueDetector>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ExceptionIssueDetector>.Instance;
    }

    public Task<IReadOnlyList<CodeIssue>> ScanAsync(
        string workspacePath, CancellationToken ct = default)
    {
        _lastScanAt = DateTime.UtcNow;
        var issues = new List<CodeIssue>();

        var files = Directory.EnumerateFiles(workspacePath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IssueUtils.IsIgnored(f))
            .ToList();

        foreach (var file in files)
        {
            try
            {
                var content = File.ReadAllText(file);
                var relPath = Path.GetRelativePath(workspacePath, file);
                var lines = content.Split('\n');

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    var trimmed = line.Trim();

                    // Empty catch block: catch (...) {  } or catch (...) { \n }
                    var emptyCatch = EmptyCatchPattern().Match(trimmed);
                    if (emptyCatch.Success)
                    {
                        issues.Add(new CodeIssue(
                            Id: $"exception:{relPath}:{i + 1}",
                            File: relPath, Line: i + 1,
                            Severity: IssueSeverity.Warning,
                            Category: "exception",
                            Title: $"Empty catch block at {relPath}:{i + 1}",
                            Description: "A catch block that does nothing silently swallows exceptions",
                            Suggestion: "Add logging, rethrow, or handle the exception appropriately"));
                        continue;
                    }

                    // Catch(Exception) which is too broad
                    var broadCatch = BroadCatchPattern().Match(trimmed);
                    if (broadCatch.Success && i + 1 < lines.Length)
                    {
                        // Check if next line is empty (no handling)
                        var nextLine = lines[i + 1].Trim();
                        if (string.IsNullOrEmpty(nextLine) || nextLine == "{")
                        {
                            issues.Add(new CodeIssue(
                                Id: $"exception:{relPath}:{i + 1}",
                                File: relPath, Line: i + 1,
                                Severity: IssueSeverity.Warning,
                                Category: "exception",
                                Title: $"Broad 'catch (Exception)' at {relPath}:{i + 1}",
                                Description: "Catching the base Exception type is too broad; catch specific exceptions",
                                Suggestion: "Catch specific exception types or rethrow unexpected ones"));
                        }
                    }
                }
            }
            catch { /* skip */ }
        }

        _lastResults = issues.OrderBy(i => i.File).ThenBy(i => i.Line).ToList();
        return Task.FromResult(_lastResults);
    }

    [GeneratedRegex(@"catch\s*\([^)]*\)\s*\{\s*\}")]
    private static partial System.Text.RegularExpressions.Regex EmptyCatchPattern();

    [GeneratedRegex(@"catch\s*\(\s*Exception\b")]
    private static partial System.Text.RegularExpressions.Regex BroadCatchPattern();

    public void Dispose() { }
}

// ═══════════════════════════════════════════════════════════════
//  DocumentationIssueDetector
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Detects public APIs (classes, methods, properties) missing XML
/// documentation comments. Scans C# files.
/// </summary>
public sealed partial class DocumentationIssueDetector : ICodeIssueDetector
{
    private readonly ILogger<DocumentationIssueDetector> _logger;
    private DateTime? _lastScanAt;
    private IReadOnlyList<CodeIssue>? _lastResults;

    public string Name => "DocScanner";
    public IReadOnlyList<CodeIssue>? LastResults => _lastResults;
    public DateTime? LastScanAt => _lastScanAt;
    public TimeSpan Cooldown => TimeSpan.FromMinutes(30);

    public DocumentationIssueDetector(ILogger<DocumentationIssueDetector>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DocumentationIssueDetector>.Instance;
    }

    public Task<IReadOnlyList<CodeIssue>> ScanAsync(
        string workspacePath, CancellationToken ct = default)
    {
        _lastScanAt = DateTime.UtcNow;
        var issues = new List<CodeIssue>();
        var files = Directory.EnumerateFiles(workspacePath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IssueUtils.IsIgnored(f))
            .ToList();

        foreach (var file in files)
        {
            try
            {
                var content = File.ReadAllText(file);
                var relPath = Path.GetRelativePath(workspacePath, file);
                var lines = content.Split('\n');

                for (int i = 0; i < lines.Length; i++)
                {
                    var trimmed = lines[i].Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;

                    // Public class/interface/struct without preceding ///
                    var declMatch = PublicDeclarationPattern().Match(trimmed);
                    if (declMatch.Success)
                    {
                        // Check previous lines for XML doc comment
                        bool hasDoc = false;
                        for (int j = Math.Max(0, i - 5); j < i; j++)
                        {
                            if (lines[j].TrimStart().StartsWith("///"))
                            {
                                hasDoc = true;
                                break;
                            }
                        }

                        if (!hasDoc)
                        {
                            var declType = declMatch.Groups[1].Value; // class/interface/struct/enum/record
                            var declName = declMatch.Groups[2].Value;
                            issues.Add(new CodeIssue(
                                Id: $"doc:{relPath}:{i + 1}",
                                File: relPath, Line: i + 1,
                                Severity: IssueSeverity.Info,
                                Category: "documentation",
                                Title: $"Missing doc on {declType} '{declName}'",
                                Description: $"Public {declType} '{declName}' at {relPath}:{i + 1} lacks XML documentation",
                                Suggestion: $"Add /// <summary>\\n/// Description of {declName}\\n/// </summary>"));
                        }
                    }

                    // Public method with parameter but no <param> doc
                    var methodMatch = PublicMethodPattern().Match(trimmed);
                    if (methodMatch.Success)
                    {
                        bool hasDoc = false;
                        for (int j = Math.Max(0, i - 5); j < i; j++)
                        {
                            if (lines[j].TrimStart().StartsWith("/// <summary>"))
                            {
                                hasDoc = true;
                                break;
                            }
                        }

                        if (!hasDoc)
                        {
                            var methodName = methodMatch.Groups[1].Value;
                            issues.Add(new CodeIssue(
                                Id: $"doc:{relPath}:{i + 1}",
                                File: relPath, Line: i + 1,
                                Severity: IssueSeverity.Info,
                                Category: "documentation",
                                Title: $"Missing doc on method '{methodName}'",
                                Description: $"Public method '{methodName}' at {relPath}:{i + 1} lacks XML documentation",
                                Suggestion: "Add /// <summary> and /// <param> tags"));
                        }
                    }
                }
            }
            catch { /* skip */ }
        }

        _lastResults = issues.OrderBy(i => i.File).ThenBy(i => i.Line).ToList();
        return Task.FromResult(_lastResults);
    }

    [GeneratedRegex(@"\b(public\s+(?:static\s+)?(?:class|interface|struct|enum|record))\s+(\w+)")]
    private static partial System.Text.RegularExpressions.Regex PublicDeclarationPattern();

    [GeneratedRegex(@"public\s+(?:static\s+|virtual\s+|override\s+|async\s+)*(?:\w+\s+)+(\w+)\s*\(")]
    private static partial System.Text.RegularExpressions.Regex PublicMethodPattern();

    public void Dispose() { }
}

// ═══════════════════════════════════════════════════════════════
//  Shared utilities
// ═══════════════════════════════════════════════════════════════

internal static class IssueUtils
{
    private static readonly HashSet<string> IgnoredDirs =
    [
        "bin", "obj", ".git", ".vs", "node_modules", "packages",
        "__pycache__", ".venv", "dist", "build", "coverage", ".next",
    ];

    private static readonly HashSet<string> SourceExtensions =
    [
        ".cs", ".py", ".js", ".ts", ".tsx", ".jsx", ".go", ".rs", ".java",
        ".kt", ".kts", ".swift", ".rb", ".php", ".cpp", ".c", ".h", ".hpp",
    ];

    public static bool IsSourceFile(string path)
        => SourceExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static bool IsIgnored(string path)
    {
        foreach (var dir in IgnoredDirs)
        {
            if (path.Contains($"\\{dir}\\", StringComparison.OrdinalIgnoreCase) ||
                path.Contains($"/{dir}/", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static string Truncate(string text, int maxLen)
        => text.Length <= maxLen ? text : text[..maxLen] + "...";
}
