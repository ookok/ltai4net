// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  EditLedger — session-level file edit tracker
//
//  Inspired by zap-coding-agent's edited_files HashMap.
//  Tracks which files have been modified during the current session
//  and injects a compact summary into the system prompt.
//
//  This helps the agent remember what files it has touched even
//  after those turns slide out of the sliding window.
//
//  Usage:
//    EditLedger.RecordEdit("src/Program.cs")
//    EditLedger.RecordEdit("src/Utils.cs", isNew: true)
//    var summary = EditLedger.GetSummary()
//    // → "📝 Edited files (3):\n  src/Program.cs (2 edits)\n  src/Utils.cs (new)"
// ═══════════════════════════════════════════════════════════════

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LTAI.Agent;

/// <summary>
/// Session-level file edit tracker. Thread-safe singleton.
/// Records every file write/edit during a session and provides
/// a compact textual summary for system prompt injection.
/// </summary>
public sealed class EditLedger
{
    private sealed record FileEntry(int FirstTurn, int EditCount, bool IsNew);

    private readonly ConcurrentDictionary<string, FileEntry> _files = new(StringComparer.OrdinalIgnoreCase);
    private int _turnCounter;

    /// <summary>Get the singleton instance.</summary>
    public static EditLedger Default { get; } = new();

    /// <summary>
    /// Record a file edit. Called by file write/edit tools.
    /// </summary>
    /// <param name="filePath">Relative or absolute path to the edited file.</param>
    /// <param name="isNew">True if the file was newly created (not just modified).</param>
    public void RecordEdit(string filePath, bool isNew = false)
    {
        var turn = Interlocked.Increment(ref _turnCounter);
        _files.AddOrUpdate(filePath,
            _ => new FileEntry(turn, 1, isNew),
            (_, existing) => new FileEntry(
                existing.FirstTurn,
                existing.EditCount + 1,
                existing.IsNew || isNew));
    }

    /// <summary>Clear all entries (start of new session).</summary>
    public void Reset()
    {
        _files.Clear();
        Interlocked.Exchange(ref _turnCounter, 0);
    }

    /// <summary>
    /// Get number of tracked files.
    /// </summary>
    public int Count => _files.Count;

    /// <summary>
    /// Generate a compact textual summary of all edited files.
    /// Returns null if no files have been edited.
    /// </summary>
    public string? GetSummary()
    {
        if (_files.IsEmpty) return null;

        var sb = new StringBuilder();
        sb.AppendLine("## Edit Ledger");
        sb.AppendLine("*Files modified in this session:*");

        // Sort by first edit turn (most recent first makes sense for continuity)
        var sorted = _files
            .OrderByDescending(f => f.Value.FirstTurn)
            .ToList();

        foreach (var (path, entry) in sorted)
        {
            var ops = entry.EditCount > 1 ? $" ({entry.EditCount} edits)" : "";
            var label = entry.IsNew ? " [new]" : "";
            sb.AppendLine($"- `{path}`{label}{ops}");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// Estimate token cost of the ledger summary.
    /// </summary>
    public int EstimatedTokens
    {
        get
        {
            var summary = GetSummary();
            return summary != null ? (summary.Length / 4) + 20 : 0;
        }
    }
}
