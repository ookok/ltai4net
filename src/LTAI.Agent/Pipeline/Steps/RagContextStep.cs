// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  RagContextStep — injects knowledge graph context into the request
//
//  Phase 3b: wraps MemoryExtractor + intent-aware multi-graph retrieval.
//  Enriches the pipeline context with relevant RAG context from
//  the multi-graph memory system (MultiGraphStore).
// ═══════════════════════════════════════════════════════════════

using LTAI.Agent.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that injects RAG context from the knowledge graph.
/// Calls MemoryExtractor to extract and surface relevant information
/// before the request reaches the router/LLM. If a <see cref="AdaptiveBeamTraverser"/>
/// is available, performs intent-aware graph traversal for richer context.
/// </summary>
public sealed class RagContextStep : IPipelineStep
{
    private readonly Memory.MemoryExtractor? _memoryExtractor;
    private readonly Memory.MultiGraphStore? _multiGraph;
    private readonly Memory.AdaptiveBeamTraverser? _traverser;
    private readonly Memory.SalienceBudgetCompressor? _compressor;
    private readonly Memory.QueryClassifier? _queryClassifier;
    private readonly ILogger<RagContextStep> _logger;

    public string Name => "RagContext";

    public RagContextStep(
        Memory.MemoryExtractor? memoryExtractor = null,
        Memory.MultiGraphStore? multiGraph = null,
        Memory.AdaptiveBeamTraverser? traverser = null,
        Memory.SalienceBudgetCompressor? compressor = null,
        Memory.QueryClassifier? queryClassifier = null,
        ILogger<RagContextStep>? logger = null)
    {
        _memoryExtractor = memoryExtractor;
        _multiGraph = multiGraph;
        _traverser = traverser;
        _compressor = compressor;
        _queryClassifier = queryClassifier;
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

            // Step 1: Extract facts from current turn (Fast Path — run regardless of graph availability)
            await _memoryExtractor.ExtractFromTurnAsync(
                context.Request,
                entityId: context.TraceId,
                ct: context.CancellationToken)
                .ConfigureAwait(false);

            // Step 2: Intent-aware graph retrieval (only if graph infrastructure is available)
            if (_traverser != null && _compressor != null && _multiGraph != null)
            {
                var intent = _queryClassifier?.ClassifyIntent(context.Request)
                    ?? new IntentRouter().Classify(context.Request);

                // Use request keywords as entry points into the graph
                var keywords = context.Request
                    .Split([' ', '，', '。', '！', '？'], StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length > 2)
                    .Take(5)
                    .ToList();

                var seen = new HashSet<string>();
                var allResults = new List<TraversalResult>();

                foreach (var kw in keywords)
                {
                    foreach (var nodeId in _multiGraph.SearchContent(kw, 3))
                    {
                        if (!seen.Add(nodeId)) continue;
                        allResults.AddRange(_traverser.Traverse(nodeId, intent, 5));
                    }
                }

                if (allResults.Count > 0)
                {
                    var compressed = _compressor.Compress(allResults, context.Request);
                    context.Messages.Add(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, $"[记忆上下文]\n{compressed}"));
                    _logger.LogDebug("RagContextStep: added {Count} memory items from intent={Intent}", allResults.Count, intent);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RagContextStep: memory extraction failed (non-fatal)");
        }

        return context;
    }
}
