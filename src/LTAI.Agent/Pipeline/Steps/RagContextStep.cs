// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  RagContextStep — injects knowledge graph context into the request
//
//  Phase 3b: wraps MemoryExtractor.ExtractFromTurnAsync logic.
//  Enriches the pipeline context with relevant RAG context from
//  the knowledge graph (KbGraph, KgStore).
// ═══════════════════════════════════════════════════════════════

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that injects RAG context from the knowledge graph.
/// Calls MemoryExtractor to extract and surface relevant information
/// before the request reaches the router/LLM.
/// </summary>
public sealed class RagContextStep : IPipelineStep
{
    private readonly Memory.MemoryExtractor? _memoryExtractor;
    private readonly ILogger<RagContextStep> _logger;

    public string Name => "RagContext";

    public RagContextStep(
        Memory.MemoryExtractor? memoryExtractor = null,
        ILogger<RagContextStep>? logger = null)
    {
        _memoryExtractor = memoryExtractor;
        _logger = logger ?? NullLogger<RagContextStep>.Instance;
    }

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        if (_memoryExtractor == null)
        {
            _logger.LogDebug("RagContextStep: no MemoryExtractor registered, skipping");
            return context;
        }

        try
        {
            _logger.LogDebug("RagContextStep: extracting memory from request");
            await _memoryExtractor.ExtractFromTurnAsync(
                context.Request,
                entityId: context.TraceId,
                ct: context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RagContextStep: memory extraction failed (non-fatal)");
        }

        return context;
    }
}
