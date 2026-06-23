// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  CrdtText — RGA-inspired block-level CRDT for concurrent editing
//
//  RGA (Replicated Growable Array) with block-level granularity.
//  Each edit operation (insert/delete block) has a unique position
//  identifier: (siteId, clock, originLeft, originRight).
//  Concurrent insertions at the same position are resolved
//  deterministically by siteId ordering.
//
//  Block ≈ logical line group (contiguous lines written in one operation).
//  This avoids character-level overhead while maintaining CRDT guarantees.
// ═══════════════════════════════════════════════════════════════

namespace LTAI.Agent.Delta;

public sealed class CrdtBlock
{
    public string Id { get; init; } = "";
    public string SiteId { get; init; } = "";
    public int Clock { get; init; }
    public string? OriginLeft { get; init; }
    public string? OriginRight { get; init; }
    public string Content { get; set; } = "";
    public int LineStart { get; set; }
    public int LineCount { get; set; }
    public bool IsDeleted { get; set; }
}

public sealed class CrdtText
{
    private readonly List<CrdtBlock> _blocks = [];
    private readonly object _lock = new();

    public string FilePath { get; }

    public CrdtText(string filePath)
    {
        FilePath = filePath;
    }

    public int BlockCount
    {
        get { lock (_lock) return _blocks.Count; }
    }

    public int ActiveBlockCount
    {
        get { lock (_lock) return _blocks.Count(b => !b.IsDeleted); }
    }

    public void LoadFromLines(string[] lines)
    {
        lock (_lock)
        {
            _blocks.Clear();
            // Flatten into a single initial block
            var content = string.Join("\n", lines);
            _blocks.Add(new CrdtBlock
            {
                Id = "root",
                SiteId = "init",
                Clock = 0,
                OriginLeft = null,
                OriginRight = null,
                Content = content,
                LineStart = 1,
                LineCount = lines.Length,
                IsDeleted = false,
            });
        }
    }

    public void LoadFromBlocks(List<CrdtBlock> blocks)
    {
        lock (_lock)
        {
            _blocks.Clear();
            _blocks.AddRange(blocks);
            ReindexLines();
        }
    }

    public List<CrdtBlock> GetBlocks()
    {
        lock (_lock) return [.. _blocks];
    }

    public List<CrdtBlock> GetActiveBlocks()
    {
        lock (_lock) return _blocks.Where(b => !b.IsDeleted).ToList();
    }

    public string GetFullText()
    {
        lock (_lock)
        {
            var parts = _blocks
                .Where(b => !b.IsDeleted)
                .Select(b => b.Content);
            return string.Join("\n", parts);
        }
    }

    public string[] GetLines()
    {
        lock (_lock)
        {
            return _blocks
                .Where(b => !b.IsDeleted)
                .SelectMany(b => b.Content.Split('\n'))
                .ToArray();
        }
    }

    public CrdtOpResult InsertBlock(string siteId, int clock, string? afterBlockId, string content, string? beforeBlockId = null)
    {
        lock (_lock)
        {
            var blockId = $"{siteId}:{clock}";
            var newBlock = new CrdtBlock
            {
                Id = blockId,
                SiteId = siteId,
                Clock = clock,
                OriginLeft = afterBlockId,
                OriginRight = beforeBlockId,
                Content = content,
                IsDeleted = false,
            };

            int insertIdx;
            if (afterBlockId == null)
            {
                insertIdx = 0;
            }
            else
            {
                var afterIdx = _blocks.FindIndex(b => b.Id == afterBlockId);
                if (afterIdx < 0)
                    return new CrdtOpResult(false, "Block not found: " + afterBlockId);

                if (beforeBlockId != null)
                {
                    var beforeIdx = _blocks.FindIndex(b => b.Id == beforeBlockId);
                    if (beforeIdx >= 0 && beforeIdx > afterIdx + 1)
                    {
                        insertIdx = beforeIdx;
                    }
                    else
                    {
                        insertIdx = afterIdx + 1;
                    }
                }
                else
                {
                    insertIdx = afterIdx + 1;
                }
            }

            _blocks.Insert(insertIdx, newBlock);
            ReindexLines();
            return new CrdtOpResult(true, blockId);
        }
    }

    public CrdtOpResult DeleteBlock(string blockId)
    {
        lock (_lock)
        {
            var block = _blocks.FirstOrDefault(b => b.Id == blockId);
            if (block == null)
                return new CrdtOpResult(false, "Block not found: " + blockId);
            if (block.IsDeleted)
                return new CrdtOpResult(false, "Block already deleted");

            block.IsDeleted = true;
            ReindexLines();
            return new CrdtOpResult(true, blockId);
        }
    }

    public CrdtOpResult UpdateBlock(string blockId, string newContent)
    {
        lock (_lock)
        {
            var block = _blocks.FirstOrDefault(b => b.Id == blockId);
            if (block == null)
                return new CrdtOpResult(false);
            block.Content = newContent;
            ReindexLines();
            return new CrdtOpResult(true);
        }
    }

    public void MergeRemoteOp(CrdtBlock remoteBlock)
    {
        lock (_lock)
        {
            var existing = _blocks.FirstOrDefault(b => b.Id == remoteBlock.Id);
            if (existing != null)
            {
                if (remoteBlock.IsDeleted && !existing.IsDeleted)
                    existing.IsDeleted = true;
                return;
            }

            int insertIdx;
            if (remoteBlock.OriginLeft == null)
            {
                insertIdx = 0;
            }
            else
            {
                var leftIdx = _blocks.FindIndex(b => b.Id == remoteBlock.OriginLeft);
                if (leftIdx < 0)
                {
                    _blocks.Add(remoteBlock);
                    ReindexLines();
                    return;
                }

                if (remoteBlock.OriginRight != null)
                {
                    var rightIdx = _blocks.FindIndex(b => b.Id == remoteBlock.OriginRight);
                    if (rightIdx >= 0 && rightIdx > leftIdx + 1)
                        insertIdx = rightIdx;
                    else
                        insertIdx = leftIdx + 1;
                }
                else
                {
                    insertIdx = leftIdx + 1;
                }

                while (insertIdx < _blocks.Count)
                {
                    var candidate = _blocks[insertIdx];
                    if (candidate.Id == remoteBlock.Id) return;
                    if (candidate.OriginLeft != remoteBlock.OriginLeft) break;
                    if (candidate.OriginRight != remoteBlock.OriginRight) break;
                    if (string.Compare(candidate.SiteId, remoteBlock.SiteId, StringComparison.Ordinal) > 0) break;
                    insertIdx++;
                }
            }

            _blocks.Insert(insertIdx, remoteBlock);
            ReindexLines();
        }
    }

    public void MergeRemoteOps(List<CrdtBlock> remoteBlocks)
    {
        foreach (var b in remoteBlocks)
            MergeRemoteOp(b);
    }

    private void ReindexLines()
    {
        var line = 1;
        foreach (var block in _blocks)
        {
            if (block.IsDeleted) continue;
            block.LineStart = line;
            block.LineCount = block.Content.Split('\n').Length;
            line += block.LineCount;
        }
    }

    public CrdtSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new CrdtSnapshot
            {
                FilePath = FilePath,
                Blocks = [.. _blocks],
                LineCount = GetLines().Length,
                BlockCount = _blocks.Count,
                ActiveBlockCount = ActiveBlockCount,
            };
        }
    }
}

public sealed record CrdtOpResult(bool Success, string? BlockId = null, string? Error = null);

public sealed class CrdtSnapshot
{
    public string FilePath { get; init; } = "";
    public List<CrdtBlock> Blocks { get; init; } = [];
    public int LineCount { get; init; }
    public int BlockCount { get; init; }
    public int ActiveBlockCount { get; init; }
}
