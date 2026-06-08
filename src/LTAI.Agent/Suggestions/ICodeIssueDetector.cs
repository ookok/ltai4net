// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  ICodeIssueDetector — code issue detection interface
//
//  Inspiration: TIDE (arXiv 2606.04743)
//
//  Detects common code issues in workspace files:
//    - Stale TODOs (older than N days)
//    - Unhandled exceptions in catch blocks
//    - Naming convention violations
//    - Missing documentation on public APIs
//    - Long methods / high cyclomatic complexity
//    - Magic numbers / hardcoded strings
// ═══════════════════════════════════════════════════════════════

namespace LTAI.Agent.Suggestions;

/// <summary>
/// Detects code issues in workspace files and produces actionable suggestions.
/// Runs as a background service or pipeline step when the user is idle.
/// </summary>
public interface ICodeIssueDetector : IDisposable
{
    /// <summary>Name of the detector (e.g. "CSharp", "Todo", "Naming").</summary>
    string Name { get; }

    /// <summary>
    /// Scan a workspace directory and find issues.
    /// </summary>
    /// <param name="workspacePath">Root path of the codebase.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of detected issues.</returns>
    Task<IReadOnlyList<CodeIssue>> ScanAsync(
        string workspacePath, CancellationToken ct = default);

    /// <summary>
    /// Get cached scan results without re-scanning.
    /// </summary>
    IReadOnlyList<CodeIssue>? LastResults { get; }

    /// <summary>
    /// When the last scan completed.
    /// </summary>
    DateTime? LastScanAt { get; }

    /// <summary>
    /// Minimum interval between scans (to avoid re-scanning too frequently).
    /// </summary>
    TimeSpan Cooldown { get; }
}

/// <summary>
/// A single code issue detected in the workspace.
/// </summary>
/// <param name="Id">Unique ID (for deduplication).</param>
/// <param name="File">Relative file path from workspace root.</param>
/// <param name="Line">Line number (1-based, 0 = unknown).</param>
/// <param name="Severity">How critical this issue is.</param>
/// <param name="Category">Category: todo, exception, naming, complexity, documentation, magic.</param>
/// <param name="Title">Short title (≤80 chars).</param>
/// <param name="Description">Detailed description.</param>
/// <param name="Suggestion">Suggested fix.</param>
public sealed record CodeIssue(
    string Id,
    string File,
    int Line,
    IssueSeverity Severity,
    string Category,
    string Title,
    string Description,
    string? Suggestion = null);

/// <summary>Issue severity level.</summary>
public enum IssueSeverity
{
    Info,
    Warning,
    Critical,
}
