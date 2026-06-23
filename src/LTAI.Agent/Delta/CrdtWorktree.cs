// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  CrdtWorktree — conflict-free replicated worktree (DeltaDB-inspired)
//
//  Manages a set of files as CRDT-backed documents. Multiple agents
//  can edit the same file concurrently across different sites/processes.
//  Conflicts are resolved deterministically via RGA block ordering.
//
//  Each file is a CrdtText instance. Operations are logged to the
//  DeltaStore's worktree_ops table for persistence and sync.
//
//  Integration:
//    - FileSystemTools and PatchEditTool call into CrdtWorktree
//      before applying edits
//    - On read, the worktree returns the merged state from all
//      concurrent edits
//    - Periodic background sync with other sites via DeltaStore
// ═══════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Delta;

public sealed class CrdtWorktree : IDisposable
{
    private readonly ConcurrentDictionary<string, CrdtText> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly DeltaStore _deltaStore;
    private readonly ILogger<CrdtWorktree> _logger;
    private int _clock;

    public string SiteId => _deltaStore.SiteId;

    public CrdtWorktree(DeltaStore deltaStore, ILogger<CrdtWorktree>? logger = null)
    {
        _deltaStore = deltaStore;
        _logger = logger ?? NullLogger<CrdtWorktree>.Instance;
    }

    public CrdtText GetOrCreateDocument(string filePath)
    {
        return _documents.GetOrAdd(filePath, path =>
        {
            var doc = new CrdtText(path);
            if (File.Exists(path))
            {
                try
                {
                    var lines = File.ReadAllLines(path);
                    doc.LoadFromLines(lines);
                    _logger.LogDebug("CrdtWorktree: loaded {Path} ({N} lines)", path, lines.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "CrdtWorktree: failed to load {Path}", path);
                }
            }
            return doc;
        });
    }

    public async Task<CrdtOpResult> ApplyEditAsync(
        string filePath, string newContent,
        string? conversationId, string? messageId,
        string? agentId = null)
    {
        var doc = GetOrCreateDocument(filePath);
        var clock = Interlocked.Increment(ref _clock);
        var blockId = $"{_deltaStore.SiteId}:{clock}";

        var oldLines = doc.GetLines();
        var oldBlocks = doc.GetActiveBlocks();

        var lastBlockId = oldBlocks.Count > 0 ? oldBlocks[^1].Id : null;

        var result = doc.InsertBlock(_deltaStore.SiteId, clock, lastBlockId, newContent);
        if (!result.Success)
            return result;

        foreach (var block in oldBlocks)
            doc.DeleteBlock(block.Id);

        try
        {
            var diffContent = ComputeDiff(oldLines, newContent.Split('\n'));
            await _deltaStore.CreateDeltaForEditAsync(
                filePath, startLine: 1,
                endLine: newContent.Split('\n').Length,
                diffContent: diffContent,
                toolName: "CrdtWorktree.ApplyEdit",
                conversationId: conversationId ?? "unknown",
                messageId: messageId ?? "unknown",
                agentId: agentId,
                isNewFile: !File.Exists(filePath));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CrdtWorktree: failed to record delta for {Path}", filePath);
        }

        return new CrdtOpResult(true, blockId);
    }

    public async Task<CrdtOpResult> ApplyPatchAsync(
        string filePath, List<TextEdit> edits,
        string? conversationId, string? messageId,
        string? agentId = null)
    {
        var doc = GetOrCreateDocument(filePath);
        var lines = doc.GetLines().ToList();

        foreach (var edit in edits.OrderByDescending(e => e.StartLine))
        {
            if (edit.StartLine < 1 || edit.StartLine > lines.Count) continue;

            var startIdx = edit.StartLine - 1;
            var endIdx = Math.Min(edit.EndLine, lines.Count);

            if (edit.DeleteCount > 0)
            {
                var deleteEnd = Math.Min(startIdx + edit.DeleteCount, lines.Count);
                lines.RemoveRange(startIdx, deleteEnd - startIdx);
            }

            if (!string.IsNullOrEmpty(edit.NewText))
            {
                var newLines = edit.NewText.Split('\n');
                lines.InsertRange(startIdx, newLines);
            }
        }

        var newContent = string.Join("\n", lines);
        var clock = Interlocked.Increment(ref _clock);
        var blockId = $"{_deltaStore.SiteId}:{clock}";

        var oldBlocks = doc.GetActiveBlocks();
        var lastBlockId = oldBlocks.Count > 0 ? oldBlocks[^1].Id : null;

        var result = doc.InsertBlock(_deltaStore.SiteId, clock, lastBlockId, newContent);
        if (!result.Success) return result;

        foreach (var block in oldBlocks)
            doc.DeleteBlock(block.Id);

        try
        {
            var diffContent = string.Join("\n", edits.Select(e => $"@@ -{e.StartLine},{e.DeleteCount} +{e.StartLine},{e.NewLineCount} @@\n{e.NewText}"));
            await _deltaStore.CreateDeltaForEditAsync(
                filePath, edits.Min(e => e.StartLine),
                edits.Max(e => e.EndLine),
                diffContent, "CrdtWorktree.ApplyPatch",
                conversationId ?? "unknown", messageId ?? "unknown",
                agentId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CrdtWorktree: failed to record delta for {Path}", filePath);
        }

        return new CrdtOpResult(true, blockId);
    }

    public string ReadFile(string filePath)
    {
        if (_documents.TryGetValue(filePath, out var doc))
            return doc.GetFullText();

        if (File.Exists(filePath))
            return File.ReadAllText(filePath);

        return "";
    }

    public string[] ReadLines(string filePath)
    {
        if (_documents.TryGetValue(filePath, out var doc))
            return doc.GetLines();

        if (File.Exists(filePath))
            return File.ReadAllLines(filePath);

        return [];
    }

    public void FlushDocument(string filePath)
    {
        if (!_documents.TryGetValue(filePath, out var doc)) return;
        var text = doc.GetFullText();
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(filePath, text);
    }

    public void FlushAll()
    {
        foreach (var (path, _) in _documents)
        {
            try { FlushDocument(path); }
            catch (Exception ex) { _logger.LogWarning(ex, "CrdtWorktree: failed to flush {Path}", path); }
        }
    }

    public CrdtSnapshot GetSnapshot(string filePath)
    {
        var doc = GetOrCreateDocument(filePath);
        return doc.GetSnapshot();
    }

    public Dictionary<string, CrdtSnapshot> GetAllSnapshots()
    {
        var result = new Dictionary<string, CrdtSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, doc) in _documents)
            result[path] = doc.GetSnapshot();
        return result;
    }

    public void Dispose()
    {
        FlushAll();
    }

    private static string ComputeDiff(string[] oldLines, string[] newLines)
    {
        var sb = new StringBuilder();
        int maxLen = Math.Min(oldLines.Length, newLines.Length);
        int diffStart = -1;

        for (int i = 0; i < maxLen; i++)
        {
            if (oldLines[i] != newLines[i])
            {
                if (diffStart < 0) diffStart = i;
            }
        }

        if (diffStart >= 0)
        {
            int oldEnd = oldLines.Length - 1;
            int newEnd = newLines.Length - 1;
            while (oldEnd > diffStart && newEnd > diffStart && oldLines[oldEnd] == newLines[newEnd])
            {
                oldEnd--;
                newEnd--;
            }

            sb.AppendLine($"@@ -{diffStart + 1},{oldEnd - diffStart + 1} +{diffStart + 1},{newEnd - diffStart + 1} @@");
            for (int i = diffStart; i <= oldEnd; i++)
                sb.AppendLine("-" + oldLines[i]);
            for (int i = diffStart; i <= newEnd; i++)
                sb.AppendLine("+" + newLines[i]);
        }
        else if (oldLines.Length != newLines.Length)
        {
            sb.AppendLine($"@@ -{oldLines.Length + 1},0 +{oldLines.Length + 1},{newLines.Length - oldLines.Length} @@");
            for (int i = oldLines.Length; i < newLines.Length; i++)
                sb.AppendLine("+" + newLines[i]);
        }

        return sb.ToString();
    }

    internal int Clock => _clock;
}

public sealed record TextEdit(
    int StartLine,
    int EndLine,
    int DeleteCount,
    string NewText,
    int NewLineCount)
{
    public static TextEdit Replace(int startLine, int endLine, string newText)
    {
        var lineCount = newText.Split('\n').Length;
        return new TextEdit(startLine, endLine, endLine - startLine + 1, newText, lineCount);
    }

    public static TextEdit Insert(int afterLine, string newText)
    {
        var lineCount = newText.Split('\n').Length;
        return new TextEdit(afterLine + 1, afterLine, 0, newText, lineCount);
    }

    public static TextEdit Delete(int startLine, int endLine)
    {
        return new TextEdit(startLine, endLine, endLine - startLine + 1, "", 0);
    }
}
