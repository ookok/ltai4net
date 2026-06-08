// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  VectorStoreFactory — DI factory for IVectorStore implementations
//
//  Phase 1a: reads LTAI:Vector:Store config to select the active
//  IVectorStore backend. Default is "hnsw" (in-memory HNSW with
//  TurboQuant 4-bit). Future backends: "pgvector" (PostgreSQL),
//  "qdrant", "memory" (brute-force), etc.
// ═══════════════════════════════════════════════════════════════

using System;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LTAI.Agent.Vector;

/// <summary>
/// Creates and configures an <see cref="IVectorStore"/> implementation
/// based on the <c>LTAI:Vector:Store</c> configuration key.
///
/// Registered in DI as a singleton factory. Callers (KgStore, KbGraph,
/// CodeChunkIndex, CgGraph) get the shared IVectorStore via DI.
/// </summary>
public static class VectorStoreFactory
{
    /// <summary>
    /// Known store backend identifiers.
    /// </summary>
    public static class Backends
    {
        public const string Hnsw = "hnsw";
        // Future:
        // public const string PgVector = "pgvector";
        // public const string Qdrant = "qdrant";
        // public const string MemoryBruteForce = "memory";
    }

    /// <summary>
    /// Create an <see cref="IVectorStore"/> based on configuration.
    /// </summary>
    /// <param name="options">LTAI options containing Vector config.</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>A configured IVectorStore instance.</returns>
    public static IVectorStore Create(
        IOptions<LTAIOptions> options,
        ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        var cfg = options?.Value?.Vector;
        var backend = cfg?.Store ?? Backends.Hnsw;

        logger.LogInformation("VectorStoreFactory: creating backend '{Backend}'", backend);

        return backend.ToLowerInvariant() switch
        {
            Backends.Hnsw => new HnswVectorStore(),
            _ => throw new ArgumentException($"Unknown vector store backend: '{backend}'. " +
                $"Valid: {Backends.Hnsw}")
        };
    }
}
