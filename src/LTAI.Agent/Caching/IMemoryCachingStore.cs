// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  IMemoryCachingStore — conversation state checkpoint store
//
//  Inspiration: Memory Caching (arXiv 2602.24281)
//  "RNNs with Growing Memory via State Checkpointing"
//
//  Unlike IKvCacheStore (LLM response cache) and PrefixKvCache
//  (SHA-256 prefix cache), this store caches conversation state
//  checkpoints: tokenized context windows, KV cache snapshots,
//  conversation position summaries. Designed for long-running
//  agent conversations (50+ turns, 10k+ tokens).
//
//  Three-layer cascade:
//    Tier 1: In-memory (fast LRU, 64 entries)
//    Tier 2: SQLite (persistent, unlimited)
//    Tier 3: Null (no-op, graceful degradation)
// ═══════════════════════════════════════════════════════════════

namespace LTAI.Agent.Caching;

/// <summary>
/// Conversation state checkpoint store. Caches serialized conversation
/// state (tokenized tokens, KV cache snapshot metadata, context position)
/// for efficient restoration in long-running conversations.
/// </summary>
public interface IMemoryCachingStore : IDisposable
{
    /// <summary>Name of the active tier (Memory / Sqlite / Null).</summary>
    string ActiveTier { get; }

    /// <summary>Number of checkpoints in cache.</summary>
    int CheckpointCount { get; }

    /// <summary>
    /// Save a conversation state checkpoint.
    /// </summary>
    /// <param name="key">Unique key (e.g. "session:{sessionId}:pos:{tokenCount}").</param>
    /// <param name="data">Serialized checkpoint data.</param>
    /// <param name="tokenCount">Total tokens at checkpoint position.</param>
    /// <param name="ct">Cancellation token.</param>
    Task StoreAsync(string key, byte[] data, long tokenCount, CancellationToken ct = default);

    /// <summary>
    /// Look up a checkpoint by exact key.
    /// </summary>
    Task<byte[]?> LookupAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Find the nearest checkpoint before a given token count.
    /// Used to restore from the latest state when a new query arrives.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="tokenCount">Current token count (search for nearest checkpoint <= this).</param>
    /// <returns>Nearest checkpoint key and data, or null if none found.</returns>
    Task<(string key, byte[] data, long tokenCount)?> FindNearestAsync(
        string sessionId, long tokenCount, CancellationToken ct = default);

    /// <summary>
    /// Find checkpoint summaries in a token range (for conversation wind-back).
    /// </summary>
    Task<IReadOnlyList<CheckpointSummary>> FindRangeAsync(
        string sessionId, long fromToken, long toToken, CancellationToken ct = default);

    /// <summary>
    /// Delete checkpoints for a session.
    /// </summary>
    Task InvalidateSessionAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Remove all checkpoints.</summary>
    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>
/// Lightweight summary of a checkpoint position.
/// </summary>
/// <param name="Key">Checkpoint key.</param>
/// <param name="TokenCount">Tokens processed at this checkpoint.</param>
/// <param name="SavedAt">When the checkpoint was created.</param>
/// <param name="Tier">Which tier stored it (memory/sqlite).</param>
public sealed record CheckpointSummary(
    string Key,
    long TokenCount,
    DateTime SavedAt,
    string Tier);
