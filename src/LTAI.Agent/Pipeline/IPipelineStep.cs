// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  IPipelineStep — pipeline step interface
//
//  Phase 3a: every step in the message processing pipeline
//  implements this interface. Steps are composed via DI or manual instantiation.
// ═══════════════════════════════════════════════════════════════

namespace LTAI.Agent.Pipeline;

/// <summary>
/// A single processing step in the LTAI message pipeline.
/// Each step receives a <see cref="MessageContext"/>, processes it,
/// and returns the (potentially modified) context.
/// </summary>
public interface IPipelineStep
{
    /// <summary>Display name for telemetry and debugging.</summary>
    string Name { get; }

    /// <summary>
    /// Process the message context. The step may:
    ///   - Read/validate the request
    ///   - Enrich with context (RAG, memory)
    ///   - Route to execution engine
    ///   - Filter for safety
    ///   - Execute tools
    ///   - Compress history
    /// </summary>
    /// <param name="context">Mutable message context.</param>
    /// <returns>The processed context (same instance, modified in-place).</returns>
    Task<MessageContext> ProcessAsync(MessageContext context);
}
