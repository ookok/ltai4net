// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  DeltaEntry — fine-grained operation record (DeltaDB-inspired)
//
//  Every agent file edit is recorded as an addressable delta.
//  Deltas form a DAG via ParentId, enabling full operation history.
//  Each delta is content-addressed by its SHA256 hash.
// ═══════════════════════════════════════════════════════════════

using System.Text.Json;

namespace LTAI.Agent.Delta;

public sealed record DeltaEntry
{
    public string Id { get; init; } = "";
    public string? ParentId { get; init; }
    public string ConversationId { get; init; } = "";
    public string MessageId { get; init; } = "";
    public string ToolName { get; init; } = "";
    public string FilePath { get; init; } = "";
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public long Timestamp { get; init; }
    public string? AgentId { get; init; }
    public string? DiffContent { get; init; }
    public bool IsNewFile { get; init; }
    public string? Checksum { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }

    public string ToShortString() =>
        $"[delta:{Id[..12]}] {ToolName} {FilePath} L{StartLine}-L{EndLine}";
}

public sealed record DeltaChainInfo(
    string FilePath,
    int TotalDeltas,
    string? HeadDeltaId,
    string? EarliestDeltaId,
    long FirstEditAt,
    long LastEditAt);

public sealed record CodeProvenanceResult(
    string DeltaId,
    string ConversationId,
    string MessageId,
    string FilePath,
    int StartLine,
    int EndLine,
    string ToolName,
    long Timestamp);

public sealed record ConversationCodeLink(
    string FilePath,
    int StartLine,
    int EndLine,
    string DeltaId);

public sealed class DeltaStats
{
    public int TotalDeltas { get; set; }
    public int TotalFiles { get; set; }
    public int TotalConversations { get; set; }
    public int TotalAgents { get; set; }
    public long EarliestTimestamp { get; set; }
    public long LatestTimestamp { get; set; }
    public Dictionary<string, int> EditsPerFile { get; init; } = [];
    public Dictionary<string, int> EditsPerTool { get; init; } = [];
}
