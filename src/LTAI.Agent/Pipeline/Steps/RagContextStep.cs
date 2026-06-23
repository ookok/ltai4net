// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  RagContextStep — injects knowledge graph context into the request
//
//  Phase 3b: wraps MemoryExtractor + intent-aware multi-graph retrieval.
//  Phase 3c: discourse-aware context reorganization (Disco-RAG inspired).
//  Enriches the pipeline context with relevant RAG context from
//  the multi-graph memory system (MultiGraphStore), then reorganizes
//  retrieved evidence by rhetorical role (Elaboration/Contrast/Cause/Support)
//  for improved generation coherence.
// ═══════════════════════════════════════════════════════════════

using LTAI.Agent.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that injects RAG context from the knowledge graph
/// and reorganizes it with discourse-aware structure.
/// Calls MemoryExtractor to extract and surface relevant information
/// before the request reaches the router/LLM. If a <see cref="AdaptiveBeamTraverser"/>
/// is available, performs intent-aware graph traversal for richer context.
/// Then applies discourse-aware reorganization (Disco-RAG inspired) to
/// classify retrieved chunks by rhetorical role and present them as a
/// structured discourse context.
/// </summary>
public sealed class RagContextStep : IPipelineStep
{
    private readonly Memory.MemoryExtractor? _memoryExtractor;
    private readonly Memory.MultiGraphStore? _multiGraph;
    private readonly Memory.AdaptiveBeamTraverser? _traverser;
    private readonly Memory.SalienceBudgetCompressor? _compressor;
    private readonly Memory.QueryClassifier? _queryClassifier;
    private readonly IChatClient? _discourseClassifier;
    private readonly ILogger<RagContextStep> _logger;

    /// <summary>Simple discourse role keywords for heuristic classification (fast path).</summary>
    private static readonly Dictionary<string, string> DiscourseRoleKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["但是"] = "contrast",
        ["然而"] = "contrast",
        ["不过"] = "contrast",
        ["虽然"] = "contrast",
        ["相反"] = "contrast",
        ["不同的是"] = "contrast",
        ["because"] = "cause",
        ["since"] = "cause",
        ["due to"] = "cause",
        ["therefore"] = "cause",
        ["thus"] = "cause",
        ["consequently"] = "cause",
        ["因为"] = "cause",
        ["所以"] = "cause",
        ["导致"] = "cause",
        ["因此"] = "cause",
        ["for example"] = "elaboration",
        ["for instance"] = "elaboration",
        ["例如"] = "elaboration",
        ["比如"] = "elaboration",
        ["具体来说"] = "elaboration",
        ["也就是说"] = "elaboration",
        ["in conclusion"] = "conclusion",
        ["总之"] = "conclusion",
        ["综上所述"] = "conclusion",
        ["背景"] = "background",
        ["overview"] = "background",
        ["概述"] = "background",
    };

    public string Name => "RagContext";

    public RagContextStep(
        Memory.MemoryExtractor? memoryExtractor = null,
        Memory.MultiGraphStore? multiGraph = null,
        Memory.AdaptiveBeamTraverser? traverser = null,
        Memory.SalienceBudgetCompressor? compressor = null,
        Memory.QueryClassifier? queryClassifier = null,
        IChatClient? discourseClassifier = null,
        ILogger<RagContextStep>? logger = null)
    {
        _memoryExtractor = memoryExtractor;
        _multiGraph = multiGraph;
        _traverser = traverser;
        _compressor = compressor;
        _queryClassifier = queryClassifier;
        _discourseClassifier = discourseClassifier;
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
            string? discourseContext = null;
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
                    discourseContext = compressed;
                    _logger.LogDebug("RagContextStep: added {Count} memory items from intent={Intent}", allResults.Count, intent);
                }
            }

            // Step 3: Discourse-aware context reorganization (Disco-RAG inspired)
            if (discourseContext != null)
            {
                var organized = await OrganizeDiscourseAsync(discourseContext, context.Request, context.CancellationToken)
                    .ConfigureAwait(false);
                lock (context.MessagesLock)
                {
                    context.Messages.Add(new ChatMessage(ChatRole.System, $"[话语上下文]\n{organized}"));
                }
                _logger.LogDebug("RagContextStep: added discourse-organized context");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RagContextStep: memory extraction failed (non-fatal)");
        }

        return context;
    }

    /// <summary>
    /// Classify each discourse segment by rhetorical role and produce a
    /// structured context block. Uses LLM when available (slow path with
    /// richer analysis), falls back to keyword heuristic (fast path).
    /// </summary>
    private async Task<string> OrganizeDiscourseAsync(string context, string query, CancellationToken ct)
    {
        // Fast path: heuristic discourse role classification
        var segments = SplitIntoDiscourseSegments(context);
        var classified = new List<(string Segment, string Role)>();

        foreach (var seg in segments)
        {
            var role = ClassifyDiscourseRoleHeuristic(seg);
            classified.Add((seg, role));
        }

        // If LLM classifier is available, enrich heuristic with LLM analysis
        if (_discourseClassifier != null && classified.Count > 0)
        {
            try
            {
                var enriched = await EnrichWithLlmDiscourseAsync(classified, query, ct).ConfigureAwait(false);
                if (enriched != null)
                    classified = enriched;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RagContextStep: LLM discourse classification failed, using heuristic");
            }
        }

        // Build structured discourse output
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("【话语结构概述】");

        // Group by role
        var byRole = classified
            .GroupBy(c => c.Role)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Segment).ToList());

        // Order: Background → Elaboration → Cause → Support → Contrast → Conclusion
        var roleOrder = new[] { "background", "elaboration", "cause", "support", "contrast", "conclusion", "unknown" };
        foreach (var role in roleOrder)
        {
            if (byRole.TryGetValue(role, out var segs) && segs.Count > 0)
            {
                sb.AppendLine($"\n**{GetRoleLabel(role)}** ({segs.Count} 条):");
                for (int i = 0; i < segs.Count; i++)
                {
                    // Truncate very long segments
                    var text = segs[i].Length > 300 ? segs[i][..300] + "..." : segs[i];
                    sb.AppendLine($"  {i + 1}. {text}");
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Use LLM to enrich discourse classification. Sends current heuristic
    /// classifications to LLM for refinement via a compact prompt.
    /// </summary>
    private async Task<List<(string Segment, string Role)>?> EnrichWithLlmDiscourseAsync(
        List<(string Segment, string Role)> classified,
        string query,
        CancellationToken ct)
    {
        var segTexts = string.Join("\n---\n",
            classified.Select((c, i) => $"[{i}] ({c.Role}) {c.Segment[..Math.Min(c.Segment.Length, 200)]}"));

        var prompt = $@"Analyze the rhetorical structure of the following context segments retrieved for query: ""{query}""

For each segment, classify its discourse role:
- background: contextual/setting info
- elaboration: example or detail
- cause: causal relationship
- support: supporting evidence
- contrast: opposing/contrasting view
- conclusion: summary or concluding statement

Current segments:
{segTexts}

Return a JSON array of {{""index"": int, ""role"": ""...""}} only.";

        var response = await _discourseClassifier
            .GetResponseAsync([new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(response?.Text))
            return null;

        // Simple JSON array parse
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(response.Text);
            var root = doc.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var result = new List<(string Segment, string Role)>(classified.Count);
                var validRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "background", "elaboration", "cause", "support", "contrast", "conclusion" };

                foreach (var item in root.EnumerateArray())
                {
                    var idx = item.GetProperty("index").GetInt32();
                    var role = item.GetProperty("role").GetString() ?? "unknown";
                    if (idx >= 0 && idx < classified.Count)
                    {
                        role = validRoles.Contains(role) ? role : "unknown";
                        result.Add((classified[idx].Segment, role));
                    }
                }

                if (result.Count == classified.Count)
                    return result;
            }
        }
        catch
        {
            // JSON parse failure — fall through to return null
        }

        return null;
    }

    /// <summary>Keyword-based heuristic discourse role classification (fast path, zero LLM calls).</summary>
    private static string ClassifyDiscourseRoleHeuristic(string text)
    {
        var bestRole = "unknown";
        var bestScore = 0;

        foreach (var (keyword, role) in DiscourseRoleKeywords)
        {
            int count = 0;
            int idx = 0;
            while ((idx = text.IndexOf(keyword, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                idx += keyword.Length;
            }
            if (count > bestScore)
            {
                bestScore = count;
                bestRole = role;
            }
        }

        return bestRole;
    }

    /// <summary>Split context text into discourse segments (by double newlines or numbered items).</summary>
    private static List<string> SplitIntoDiscourseSegments(string text)
    {
        var segments = new List<string>();

        // Try splitting by double newlines first
        var parts = text.Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 20)
                segments.Add(trimmed);
        }

        // If no clear paragraph breaks, split by single newlines
        if (segments.Count <= 1)
        {
            segments.Clear();
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 20)
                    segments.Add(trimmed);
            }
        }

        return segments;
    }

    private static string GetRoleLabel(string role) => role switch
    {
        "background" => "背景 (Background)",
        "elaboration" => "阐述 (Elaboration)",
        "cause" => "因果 (Cause)",
        "support" => "支持 (Support)",
        "contrast" => "对比 (Contrast)",
        "conclusion" => "结论 (Conclusion)",
        _ => "其他 (Other)",
    };
}
