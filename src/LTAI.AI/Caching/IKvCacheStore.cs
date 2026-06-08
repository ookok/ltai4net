// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  IKvCacheStore — KV cache storage interface
//
//  Phase 5a: store/lookup/invalidate KV cache entries.
//  Designed for integration with MultiProviderChatClient to
//  reuse LLM KV caches across similar requests.
//
//  Two strategies:
//    - PrefixCache: SHA-256 prefix key on (system prompt + history)
//    - SemanticCache: vector similarity on query embeddings
// ═══════════════════════════════════════════════════════════════

namespace LTAI.AI.Caching;

/// <summary>
/// KV cache storage interface. Stores serialized LLM KV cache entries
/// and provides fast lookup + TTL-based invalidation.
/// </summary>
public interface IKvCacheStore : IDisposable
{
    /// <summary>Look up a cache entry by key.</summary>
    /// <param name="key">Cache key (e.g. SHA-256 prefix hash).</param>
    /// <returns>The cached bytes, or null if not found / expired.</returns>
    byte[]? Lookup(string key);

    /// <summary>Store a cache entry.</summary>
    /// <param name="key">Cache key.</param>
    /// <param name="data">Cached data (serialized KV cache).</param>
    /// <param name="ttl">Time-to-live. Default 5 minutes.</param>
    void Store(string key, byte[] data, TimeSpan? ttl = null);

    /// <summary>Invalidate a specific cache entry.</summary>
    void Invalidate(string key);

    /// <summary>Invalidate all cache entries.</summary>
    void Clear();

    /// <summary>Number of entries currently cached.</summary>
    int Count { get; }
}
