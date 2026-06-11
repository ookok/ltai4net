// Copyright (c) LTAI. All rights reserved.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LTAI.AI;
using LTAI.Agent.Tools;
using LTAI.Agent.Formats;
using LTAI.Agent.Utils;
using LTAI.Core.Vector;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Vector;

/// <summary>
/// Knowledge Base Graph (SQLite + FTS5).
/// Pipeline: LLM query rewrite → BM25 recall → CTE BFS expansion → context injection.
/// </summary>
public sealed class KbGraph : AIContextProvider, LTAI.Core.Vector.IKbQueryable
{
    private readonly KgStore _store;
    private readonly IChatClient? _rewriter;
    private readonly Reranker? _reranker;
    private readonly EmbeddingClient? _embedder;
    private readonly ILogger<KbGraph> _logger;

    /// <summary>RRF fusion constant (default 60, inspired by sqlite-graphrag's configurable --rrf-k).</summary>
    public int RrfK { get; set; } = 60;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="store">SQLite KgStore.</param>
    /// <param name="rewriter">Optional LLM for query→keyword rewriting. If null, raw query is used as-is.</param>
    /// <param name="reranker">Optional two-stage reranker (embeddings + LLM rescore).</param>
    /// <param name="logger">Logger.</param>
    public KbGraph(KgStore store, IChatClient? rewriter = null,
        Reranker? reranker = null, EmbeddingClient? embedder = null,
        ILogger<KbGraph>? logger = null)
        : base(null, null, null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _rewriter = rewriter;
        _reranker = reranker;
        _embedder = embedder;
        _logger = logger ?? NullLogger<KbGraph>.Instance;
    }

    // ═══════════════════════════════════════════
    //  Public query
    // ═══════════════════════════════════════════

    async Task<List<string>> LTAI.Core.Vector.IKbQueryable.QueryAsync(string query, int topK, CancellationToken ct)
    {
        return await QueryAsync(query, topK, true, ct, ResultFormat.Markdown).ConfigureAwait(false);
    }

    public async Task<List<string>> QueryAsync(string query, int topK = 10,
        bool expandGraph = true, CancellationToken ct = default,
        ResultFormat format = ResultFormat.Markdown)
    {
        // Stage 1: Query expansion — skip LLM rewriter for simple queries
        string expanded;
        if (query.Length <= 8 || query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 2)
        {
            expanded = query;
        }
        else
        {
            expanded = await ExpandQueryAsync(query, ct).ConfigureAwait(false);
        }
        if (string.IsNullOrWhiteSpace(expanded)) expanded = query;

        if (!string.Equals(query, expanded, StringComparison.Ordinal))
            _logger.LogInformation("KbGraph: \"{Q}\" → expanded: \"{E}\"", query, expanded);

        // Stage 2: FTS5 BM25 recall (weighted by node kind)
        var ftsHits = await _store.SearchFts(expanded, topN: topK * 3).ConfigureAwait(false);

        // Stage 2a: Quality score boost — blend BM25 rank with quality/freshness
        // scores from KnowledgeQualityScorer. Quality scores deemphasize stale or
        // low-value documents without eliminating them entirely.
        if (ftsHits.Count > 0)
        {
            try
            {
                var scored = await BoostByQualityAsync(ftsHits).ConfigureAwait(false);
                ftsHits = scored;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "KbGraph: quality boost failed, using raw FTS5");
            }
        }

        // Stage 2b: Optional hybrid search (FTS5 + vector RRF)
        if (_reranker != null && ftsHits.Count > 0)
        {
            try
            {
                var localEmb = GetSharedEmbedder();
                if (localEmb != null && localEmb.Available)
                {
                    var queryEmb = localEmb.Generate(query);
                    var vecHits = await _store.SearchVector(queryEmb, topN: topK * 3).ConfigureAwait(false);

                    var rrf = new Dictionary<long, double>();
                    int k = RrfK;
                    int rank = 0;
                    foreach (var h in ftsHits)
                        rrf[h.nodeId] = 1.0 / (k + rank++);
                    rank = 0;
                    foreach (var (nid, _) in vecHits)
                        rrf[nid] = rrf.GetValueOrDefault(nid) + 1.0 / (k + rank++);

                    var fusedIds = rrf.OrderByDescending(x => x.Value)
                                      .Take(topK * 2)
                                      .Select(x => x.Key)
                                      .ToList();
                    var ftsMap = ftsHits.ToDictionary(h => h.nodeId);
                    ftsHits = fusedIds
                        .Select(id => ftsMap.TryGetValue(id, out var hit) ? hit : (id, "", 0.0, ""))
                        .ToList();
                    _logger.LogInformation("KbGraph: FTS5+Vector RRF fusion, {N} results", ftsHits.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "KbGraph: hybrid search failed, using FTS5 only");
            }
        }

        // Stage 3: CTE BFS expansion
        HashSet<long> resultIds;
        if (expandGraph && ftsHits.Count > 0)
        {
            var startIds = ftsHits.Take(3).Select(h => h.nodeId).ToList();
            var bfsNodes = await _store.TraverseBfs(startIds, maxDepth: 2, maxNodes: 10).ConfigureAwait(false);
            resultIds = new HashSet<long>(bfsNodes.Select(n => n.Id));
            foreach (var h in ftsHits) resultIds.Add(h.nodeId);
        }
        else
        {
            resultIds = new HashSet<long>(ftsHits.Select(h => h.nodeId));
        }

        // Stage 4: Rich mixed context output (ms graphrag LocalSearchMixedContext inspired)
        return await BuildMixedContextAsync(resultIds, topK, ct, format).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-rank FTS5 hits by blending BM25 rank with quality scores.
    /// Normalized blend: 0.7 × BM25 rank + 0.3 × quality_score (0-1).
    /// Hits without quality scores use the median quality (0.5).
    /// </summary>
    private async Task<List<(long nodeId, string text, double rank, string kind)>> BoostByQualityAsync(
        List<(long nodeId, string text, double rank, string kind)> hits)
    {
        var qualityMap = new Dictionary<long, double>();
        foreach (var (nid, _, _, _) in hits)
        {
            var scores = await _store.GetScoresAsync(nid).ConfigureAwait(false);
            qualityMap[nid] = scores?.QualityScore ?? 0.5;
        }

        var maxRank = hits.Count > 0 ? hits.Max(h => h.rank) : 1.0;
        if (maxRank <= 0) maxRank = 1.0;

        return hits
            .Select(h => (
                h.nodeId, h.text,
                rank: h.rank / maxRank * 0.7 + qualityMap.GetValueOrDefault(h.nodeId, 0.5) * 0.3,
                h.kind))
            .OrderByDescending(h => h.rank)
            .ToList();
    }

    /// <summary>
    /// Build a structured mixed context with Entities + Relationships + Text Units sections.
    /// Inspired by ms graphrag's LocalSearchMixedContext — gives the LLM clearer,
    /// more structured knowledge graph context than flat bullet points.
    /// Supports both Markdown (default) and TOON (compact, ~50% token reduction) output.
    /// </summary>
    private async Task<List<string>> BuildMixedContextAsync(HashSet<long> resultIds,
        int topK, CancellationToken ct, ResultFormat format = ResultFormat.Markdown)
    {
        var entities = new List<(NodeRow node, string? snippet)>();
        var relationships = new List<(string src, string dst, string rel, double weight)>();
        var textUnits = new List<(string source, string text)>();

        foreach (var nodeId in resultIds.Take(topK))
        {
            var node = await _store.GetNode(nodeId).ConfigureAwait(false);
            if (node == null) continue;

            // Text units (document snippets)
            var docs = await _store.GetDocs(nodeId).ConfigureAwait(false);
            string? snippet = null;
            if (docs.Count > 0)
            {
                snippet = docs[0].Text.Length > 500 ? docs[0].Text[..500] + "…" : docs[0].Text;
                textUnits.Add((node.Name, snippet));
            }
            entities.Add((node, snippet));

            // Relationships (edges to neighbors)
            foreach (var edge in (await _store.GetEdges(nodeId).ConfigureAwait(false)).Take(5))
            {
                var neighborId = edge.Src == nodeId ? edge.Dst : edge.Src;
                var neighbor = await _store.GetNode(neighborId).ConfigureAwait(false);
                if (neighbor == null) continue;
                relationships.Add((
                    edge.Src == nodeId ? node.Name : neighbor.Name,
                    edge.Dst == nodeId ? node.Name : neighbor.Name,
                    edge.Relation, edge.Weight
                ));
            }
        }

        // Deduplicate relationships (case-insensitive)
        var seenRel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        relationships = relationships
            .Where(r => seenRel.Add($"{r.src}|{r.dst}|{r.rel}"))
            .ToList();

        var output = new List<string>();

        if (format == ResultFormat.Toon)
        {
            BuildToonContext(output, entities, relationships, textUnits);
        }
        else
        {
            BuildMarkdownContext(output, entities, relationships, textUnits);
        }

        _logger.LogInformation("KbGraph: mixed context ({Fmt}): {E} entities, {R} rels, {T} text units",
            format, entities.Count, relationships.Count, textUnits.Count);
        return output;
    }

    private static void BuildToonContext(List<string> output,
        List<(NodeRow node, string? snippet)> entities,
        List<(string src, string dst, string rel, double weight)> relationships,
        List<(string source, string text)> textUnits)
    {
        var tw = new ToonWriter();
        tw.Comment($"knowledge-graph context: {entities.Count} entities, {relationships.Count} rels, {textUnits.Count} text units");

        // Entities table: kind, name, namespace, source
        if (entities.Count > 0)
        {
            var cols = new[] { "kind", "name", "ns", "src" };
            var rows = entities.Select(e => (IReadOnlyList<string>)new[] {
                e.node.Kind, e.node.Name,
                e.node.Namespace ?? "",
                e.node.Source ?? ""
            }).ToList();
            tw.Table("entities", cols, rows);
        }

        // Relationships table: src, rel, dst, weight
        if (relationships.Count > 0)
        {
            var cols = new[] { "src", "rel", "dst", "w" };
            var rows = relationships.Select(r => (IReadOnlyList<string>)new[] {
                r.src, r.rel, r.dst, r.weight.ToString("F1")
            }).ToList();
            tw.Table("rels", cols, rows);
        }

        // Text units as key-value snippets (too long for tabular)
        if (textUnits.Count > 0)
        {
            tw.BeginObject("text_units");
            foreach (var (src, txt) in textUnits.Take(8))
            {
                var cleaned = txt.Replace("\n", " ").Replace("\r", "");
                if (cleaned.Length > 500) cleaned = cleaned[..500] + "…";
                tw.KeyValue(src, cleaned);
            }
            tw.EndObject();
        }

        output.Add(tw.ToString());
    }

    private static void BuildMarkdownContext(List<string> output,
        List<(NodeRow node, string? snippet)> entities,
        List<(string src, string dst, string rel, double weight)> relationships,
        List<(string source, string text)> textUnits)
    {
        output.Add("## Relevant Knowledge");

        // Entities section
        output.Add($"### Entities ({entities.Count})");
        foreach (var (node, snippet) in entities)
        {
            var icon = node.Kind switch
            {
                "document" => "📄", "concept" => "🏷️", "fact" => "💡",
                "class" => "🔷", "method" => "🔧", "function" => "⚙️",
                "interface" => "🔲", "enum" => "🔢", "struct" => "🏗️",
                "file" => "📁", _ => "▪️"
            };
            output.Add($"- {icon} **[{node.Kind}] {node.Name}**" +
                (string.IsNullOrEmpty(node.Namespace) ? "" : $" ({node.Namespace})"));
            if (!string.IsNullOrEmpty(node.Source))
                output.Add($"  Source: {node.Source}");
        }

        // Relationships section
        if (relationships.Count > 0)
        {
            output.Add($"### Relationships ({relationships.Count})");
            foreach (var (src, dst, rel, w) in relationships.Take(20))
                output.Add($"- **{src}** ══ *{rel}* ══ **{dst}** (w={w:F1})");
        }

        // Text Units section
        if (textUnits.Count > 0)
        {
            output.Add($"### Text Units ({textUnits.Count})");
            foreach (var (source, text) in textUnits.Take(8))
            {
                var cleaned = text.Replace("\n", " ").Replace("\r", "");
                if (cleaned.Length > 500) cleaned = cleaned[..500] + "…";
                output.Add($"- **[{source}]:** {cleaned}");
            }
        }
    }

    // ═══════════════════════════════════════════
    //  AIContextProvider
    // ═══════════════════════════════════════════

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        var msgs = context.AIContext?.Messages;
        if (msgs == null) return context.AIContext!;

        var userMsg = msgs.LastOrDefault(m => m.Role == ChatRole.User);
        if (userMsg?.Text == null || userMsg.Text.Length < 5)
            return context.AIContext!;

        // Skip KG query for casual chat — embedding-based intent classification
        if (!IsKnowledgeQuery(userMsg.Text))
        {
            _logger.LogDebug("KbGraph: skipped casual query \"{Q}\"", userMsg.Text);
            return context.AIContext!;
        }

        try
        {
            var results = await QueryAsync(userMsg.Text, topK: 5, ct: ct, format: ResultFormat.Toon).ConfigureAwait(false);
            if (results.Count == 0) return context.AIContext!;

            var block = string.Join("\n", results) + "\n";
            _logger.LogInformation("KbGraph: injected {N} items", results.Count);

            return new AIContext
            {
                Instructions = context.AIContext?.Instructions != null
                    ? context.AIContext.Instructions + "\n\n" + block
                    : block,
                Messages = context.AIContext?.Messages,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KbGraph query failed");
            return context.AIContext!;
        }
    }

    // ═══════════════════════════════════════════
    //  Ingestion
    // ═══════════════════════════════════════════

    public async Task<string> IngestDocument(string id, string title, string content,
        string source = "", string lang = "zh")
    {
        // Compute content hash for incremental detection
        var contentHash = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(content)));
        var extId = $"doc:{id}";

        // Check if already ingested with same content — skip if unchanged
        var existing = await _store.GetNodeByExtId(extId).ConfigureAwait(false);
        if (existing?.Signature == contentHash)
        {
            _logger.LogInformation("KbGraph: skipped unchanged '{Id}'", id);
            return $"Skipped (unchanged): '{title}'";
        }

        var nodeId = await _store.UpsertNode(
            extId: extId,
            kind: "document",
            name: title,
            ns: source,
            signature: contentHash,
            source: source).ConfigureAwait(false);

        // Replace docs with new content
        await _store.ReplaceDocsAsync(nodeId, [(content, lang, source)]).ConfigureAwait(false);

        // Store vector embedding for hybrid search (FTS5 + vector RRF)
        if (_embedder != null)
        {
            var embText = $"{title} {source} {content[..Math.Min(content.Length, 500)]}";
            var emb = await _embedder.GenerateAsync(embText).ConfigureAwait(false);
            await _store.InsertVectorAsync(nodeId, emb).ConfigureAwait(false);
        }

        // If re-ingesting: save version history + remove old concept edges
        if (existing != null)
        {
            var snap = new Dictionary<string, object?>
            {
                ["name"] = existing.Name,
                ["kind"] = existing.Kind,
                ["signature"] = existing.Signature,
                ["source"] = existing.Source,
            };
            await _store.SaveVersionAsync(nodeId, existing.Kind, existing.Name, snap, reason: "re-ingest")
                .ConfigureAwait(false);
            await _store.DeleteEdges(nodeId, relation: "contains").ConfigureAwait(false);
        }

        var concepts = ExtractConcepts(title, content);
        foreach (var concept in concepts.Take(15))
        {
            var cid = await _store.UpsertNode(
                extId: $"concept:{concept.ToLowerInvariant().Replace(" ", "_")}",
                kind: "concept",
                name: concept).ConfigureAwait(false);
            await _store.AddEdge(nodeId, cid, "contains").ConfigureAwait(false);
        }

        var action = existing != null ? "re-ingested" : "ingested";
        _logger.LogInformation("KbGraph: {Action} '{Id}' ({T}) with {C} concepts",
            action, id, title, concepts.Count);
        return $"{(existing != null ? "Re-ingested" : "Ingested")} '{title}' with {concepts.Count} concepts";
    }

    public async Task<string> IngestFact(string id, string content,
        string category = "general", string? sourceId = null)
    {
        var extId = $"fact:{id}";
        var contentHash = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(content)));

        // Check if already ingested with same content
        var existing = await _store.GetNodeByExtId(extId).ConfigureAwait(false);
        if (existing?.Signature == contentHash)
            return $"Skipped (unchanged): fact '{id}'";

        var props = new Dictionary<string, object?>
        {
            ["content"] = content,
            ["category"] = category
        };
        var nodeId = await _store.UpsertNode(
            extId: extId,
            kind: "fact",
            name: content.Length > 100 ? content[..100] + "…" : content,
            ns: category,
            signature: contentHash,
            props: props).ConfigureAwait(false);

        await _store.ReplaceDocsAsync(nodeId, [(content, "zh", "")]).ConfigureAwait(false);

        // Store vector embedding for hybrid search
        if (_embedder != null)
        {
            var embText = $"{content[..Math.Min(content.Length, 500)]} {category}";
            var emb = await _embedder.GenerateAsync(embText).ConfigureAwait(false);
            await _store.InsertVectorAsync(nodeId, emb).ConfigureAwait(false);
        }

        if (sourceId != null)
        {
            var src = await _store.GetNodeByExtId(sourceId).ConfigureAwait(false);
            if (src != null) await _store.AddEdge(src.Id, nodeId, "has_fact").ConfigureAwait(false);
        }

        var action = existing != null ? "Re-ingested" : "Ingested";
        return $"{action} fact '{id}'";
    }

    // ═══════════════════════════════════════════
    //  Office document indexing
    // ═══════════════════════════════════════════

    private static readonly HashSet<string> OfficeExts =
        new(StringComparer.OrdinalIgnoreCase) { ".docx", ".xlsx", ".pptx" };

    /// <summary>
    /// Ingest a single Office file (.docx / .xlsx / .pptx) into the KG store.
    /// Extracts text, chunks by logical sections (paragraphs / sheets / slides),
    /// stores as "document" nodes with concepts.
    /// </summary>
    public async Task<string> IngestOfficeFile(string filePath)
    {
        if (!File.Exists(filePath))
            return $"File not found: {filePath}";

        var ext = Path.GetExtension(filePath);
        if (!OfficeExts.Contains(ext))
            return $"Unsupported Office format: {ext}";

        string content;
        try
        {
            content = ext switch
            {
                ".docx" => OfficeDocumentReader.ExtractWordText(filePath),
                ".xlsx" => OfficeDocumentReader.ExtractExcelText(filePath),
                ".pptx" => OfficeDocumentReader.ExtractPptText(filePath),
                _ => "",
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KbGraph: failed to read {File}", filePath);
            return $"Error: {ex.Message}";
        }

        if (string.IsNullOrWhiteSpace(content))
            return "No text content found in " + Path.GetFileName(filePath);

        var fileName = Path.GetFileName(filePath);
        var relPath = filePath;

        // Chunk by logical sections (double-newline separation from extractors)
        var chunks = content.Split(["\n\n"], StringSplitOptions.RemoveEmptyEntries);

        int ingested = 0, skipped = 0, reingested = 0;
        foreach (var chunk in chunks)
        {
            var trimmed = chunk.Trim();
            if (trimmed.Length < 20) continue;

            var title = trimmed.Split('\n')[0];
            if (title.Length > 100) title = title[..100] + "…";
            var sourceLabel = $"{fileName}:{title}";
            // Stable chunk ID based on source + title for incremental detection
            var chunkId = $"office:{fileName}:{title}";

            var result = await IngestDocument(
                id: chunkId,
                title: title,
                content: trimmed,
                source: sourceLabel,
                lang: "zh").ConfigureAwait(false);

            if (result.StartsWith("Skipped")) skipped++;
            else if (result.StartsWith("Re-ingested")) reingested++;
            else ingested++;
        }

        _logger.LogInformation("KbGraph: '{F}' → {N} new, {R} re-ingested, {S} skipped",
            fileName, ingested, reingested, skipped);
        return $"'{fileName}': {ingested} new, {reingested} re-ingested, {skipped} skipped";
    }

    /// <summary>
    /// Batch-index all Office files under a directory.
    /// </summary>
    public async Task<string> BuildOfficeIndexAsync(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return $"Directory not found: {directoryPath}";

        var files = DirectoryWalker.WalkToArray(
            directoryPath,
            allowedExtensions: OfficeExts,
            skipDirNames: new(StringComparer.OrdinalIgnoreCase)
            {
                "obj", "bin", "dist", "node_modules", ".git", "packages"
            });

        if (files.Length == 0)
            return "No Office files found in " + directoryPath;

        int ok = 0, fail = 0;
        foreach (var file in files)
        {
            var result = await IngestOfficeFile(file).ConfigureAwait(false);
            if (result.StartsWith("Error")) fail++;
            else ok++;
        }

        return $"Indexed {ok} / {ok + fail} Office documents";
    }

    /// <summary>
    /// Scan a directory for all document files (.md, .txt, and Office formats),
    /// ingest each into the knowledge graph. Auto-distinguishes text vs Office
    /// files. Used by /graph init and GraphInitService.
    /// </summary>
    public async Task<string> BuildDocumentIndexAsync(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return $"Directory not found: {directoryPath}";

        var allExts = new HashSet<string>(OfficeExts, StringComparer.OrdinalIgnoreCase) { ".md", ".txt", ".json" };
        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "obj", "bin", "dist", "node_modules", ".git", "packages"
        };
        var files = DirectoryWalker.WalkToArray(
            directoryPath,
            allowedExtensions: allExts,
            skipDirNames: skipDirs);

        if (files.Length == 0)
            return "No document files found in " + directoryPath;

        int ok = 0, fail = 0, officeOk = 0, officeFail = 0, textOk = 0, textFail = 0;
        foreach (var file in files)
        {
            var ext = Path.GetExtension(file);
            if (OfficeExts.Contains(ext))
            {
                var result = await IngestOfficeFile(file).ConfigureAwait(false);
                if (result.StartsWith("Error")) officeFail++;
                else officeOk++;
            }
            else
            {
                try
                {
                    var content = await File.ReadAllTextAsync(file).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(content)) { textFail++; continue; }
                    var rel = Path.GetRelativePath(directoryPath, file).Replace('\\', '/');
                    var title = Path.GetFileNameWithoutExtension(file);
                    await IngestDocument(
                        id: rel,
                        title: title,
                        content: content,
                        source: rel).ConfigureAwait(false);
                    textOk++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "KbGraph: failed to ingest text file {File}", file);
                    textFail++;
                }
            }
        }
        ok = textOk + officeOk;
        fail = textFail + officeFail;

        _logger.LogInformation("KbGraph: document index done — {Ok} ok, {Fail} fail ({Txt} text, {Off} office)",
            ok, fail, textOk, officeOk);
        return $"Indexed: {ok} documents ({textOk} text, {officeOk} Office), {fail} failed";
    }

    // ═══════════════════════════════════════════
    //  Private
    // ═══════════════════════════════════════════

    /// <summary>
    /// LLM query expansion: generates 3 groups of search terms —
    /// core keywords, synonyms/related terms, and English equivalents (for Chinese queries).
    /// </summary>
    /// <summary>
    /// L0 short-circuit: simple queries don't trigger LLM rewrite.
    /// Delegates to shared QueryUtils.
    /// </summary>
    private static bool IsSimpleQuery(string query) => QueryUtils.IsSimpleQuery(query);

    // Query expansion cache (TTL: 5 minutes)
    private static readonly ConcurrentDictionary<string, (string Expanded, DateTime CachedAt)> _expansionCache = new();
    private static readonly TimeSpan ExpansionCacheTtl = TimeSpan.FromMinutes(5);

    private async Task<string> ExpandQueryAsync(string query, CancellationToken ct)
    {
        // L0 short-circuit: simple queries don't trigger LLM
        if (_rewriter == null || IsSimpleQuery(query)) return query;

        // Check cache first
        var cacheKey = query.Trim().ToLowerInvariant();
        if (_expansionCache.TryGetValue(cacheKey, out var cached) &&
            (DateTime.UtcNow - cached.CachedAt) < ExpansionCacheTtl)
        {
            _logger.LogDebug("KbGraph: query expansion cache hit for \"{Q}\"", query);
            return cached.Expanded;
        }

        try
        {
            var prompt = $"""
                You are a search query expander. Given a query, produce expanded search terms.
                
                Rules:
                - Group 1: Core keywords from the original query (3-5 terms)
                - Group 2: Synonyms and related technical terms (2-4 terms)
                - Group 3: If the query is Chinese, add English equivalents (1-3 terms)
                
                Return ALL terms on a single line, space-separated.
                No explanations, no numbering.
                
                Examples:
                Query: 用户登录失败
                → login failure authentication UserService error 认证 失败 用户登录
                
                Query: 内存泄漏怎么排查
                → memory leak排查 GC dump heap allocation 内存 泄漏
                
                Query: {query}
                """;
            var resp = await _rewriter.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct).ConfigureAwait(false);
            var result = resp.Text?.Trim() ?? "";
            var expanded = string.IsNullOrWhiteSpace(result) ? query : result;

            // Cache the result
            _expansionCache[cacheKey] = (expanded, DateTime.UtcNow);

            // Evict old entries periodically
            if (_expansionCache.Count > 1000)
            {
                var now = DateTime.UtcNow;
                foreach (var key in _expansionCache.Keys.ToList())
                {
                    if (_expansionCache.TryGetValue(key, out var entry) &&
                        (now - entry.CachedAt) > ExpansionCacheTtl)
                    {
                        _expansionCache.TryRemove(key, out _);
                    }
                }
            }

            return expanded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KbGraph: LLM query expansion failed, using raw query \"{Q}\"", query);
            return query;
        }
    }

    /// <summary>
    /// Centroid embeddings for knowledge-seeking vs casual chat intent classification.
    /// Uses FastEmb (zero API cost, pure math) to decide whether a query needs KG lookup.
    /// </summary>
    private static readonly string[] KnowledgeAnchors =
    [
        "查找资料 搜索文档 查询信息 寻找代码",
        "什么是 是什么 怎么用 如何使用 如何实现",
        "为什么 原因 区别 对比 分析 比较",
        "代码在哪里 函数定义 方法实现 类结构",
        "解释一下 说明 介绍 总结 概括",
        "错误 问题 故障 异常 解决 修复",
        "配置 安装 部署 设置 参数 选项",
        // ── C# /.NET ──
        "接口 API endpoint 路由 控制器",
        "类 结构体 枚举 接口 抽象类 继承 多态",
        "方法 函数 属性 字段 事件 委托 lambda",
        "配置 依赖注入 DI 中间件 服务注册 容器",
        "报错 异常 堆栈 日志 调试 断点 运行时 crash",
        "ORM 数据库 SQL 查询 事务 迁移 索引",
        "测试 单元测试 xUnit NUnit Moq 断言 mock",
        "async await Task Task.Run 异步 并行 线程",
        "LINQ 查询 表达式 IEnumerable IQueryable 集合",
        "HttpClient 请求 响应 REST API 认证 JWT",
        "内存 性能 优化 缓存 池化 GC 泄漏 分析",
        // ── Python ──
        "Python pip conda venv 虚拟环境 依赖",
        "pandas numpy matplotlib 数据分析 科学计算",
        "Django Flask FastAPI 框架 路由 中间件 视图",
        "async def await asyncio 协程 异步",
        // ── JavaScript / TypeScript / Node ──
        "JavaScript JS TypeScript TS Node.js 前端 后端",
        "React Vue Angular SPA 组件 状态管理 Redux Pinia",
        "npm yarn pnpm 包管理 依赖 构建 webpack vite",
        "async await Promise callback 回调 事件循环",
        "ESLint Prettier Babel TypeScript 类型 接口",
        // ── Rust ──
        "Rust cargo 所有权 借用 生命周期 lifetime",
        "unsafe trait impl 泛型 宏 模式匹配 match",
        "async await tokio 异步 运行时 并发",
        // ── Go ──
        "Go golang go mod 包管理 goroutine channel",
        "interface struct defer error 错误处理 并发",
        // ── DevOps & Cloud ──
        "Docker 容器 镜像 dockerfile compose 编排",
        "Kubernetes K8s pod service deployment ingress",
        "CI CD 流水线 持续集成 持续部署 GitHub Actions",
        "AWS Azure GCP 云服务 对象存储 S3 函数计算",
        "Linux 服务器 shell bash 命令 进程 文件系统",
        "Nginx 反向代理 负载均衡 SSL 证书 HTTPS",
        // ── 前端 / 样式 ──
        "HTML CSS 布局 flex grid 动画 响应式 移动端",
        "浏览器 DOM 事件 渲染 性能 缓存 跨域 CORS",
        // ── Shell / 工具链 ──
        "命令行 CLI 终端 terminal bash zsh 管道 重定向",
        "git 版本控制 commit branch merge rebase PR",
        "正则表达式 regex grep sed awk 文本处理",
        // ── 网络 / 协议 ──
        "网络 TCP IP HTTP WebSocket gRPC DNS 代理",
        "RESTful gRPC GraphQL 序列化 JSON Protobuf",
        "Socket 长连接 短连接 心跳 重连 超时",
        // ── 安全 ──
        "安全 加密 解密 SSL TLS HTTPS 证书 密钥",
        "XSS CSRF SQL注入 认证 授权 OAuth JWT SSO",
        "防火墙 入侵检测 审计 权限 沙箱 隔离",
        // ── 架构 / 设计 ──
        "架构 微服务 分布式 高可用 负载均衡 容错",
        "设计模式 单例 工厂 观察者 策略 依赖注入",
        "CQRS 事件驱动 消息队列 最终一致性 Saga",
        "数据库 关系型 NoSQL 缓存 Redis 分库分表",
        // ── 算法 / 数据结构 ──
        "算法 数据结构 排序 搜索 树 图 哈希表 栈 队列",
        "时间复杂度 空间复杂度 递归 动态规划 贪心",
        "机器学习 深度学习 神经网络 NLP CV 训练 推理",
    ];

    private static readonly string[] SkipAnchors =
    [
        "你好 您好 hi hello hey 嗨 嘿嘿",
        "谢谢 感谢 多谢 辛苦了 好的 ok 嗯 哈哈",
        "再见 拜拜 明天见 回头聊",
        "今天星期几 几点了 现在几点 今天几号",
        "1+1 一加一 算一下 计算",              // simple math
        "在吗 在不在 有空吗 测试 试一下",
        "你会做什么 你能做什么 你会写代码吗",   // capability questions
        "你会什么 你有什么功能 你能干嘛",
        "帮我个忙 帮我一下 我问你个问题",
        "你好聪明 你真厉害 你太棒了",           // compliments
        "不懂 不知道 不会 没听懂 再说一遍",
        "测试测试 只是测试 试试看",
    ];

    private static float[]? _knowledgeCentroid;
    private static float[]? _skipCentroid;
    private static readonly object _centroidLock = new();

    private static void EnsureCentroids()
    {
        if (_knowledgeCentroid != null) return;
        lock (_centroidLock)
        {
            if (_knowledgeCentroid != null) return;
            _knowledgeCentroid = ComputeCentroid(KnowledgeAnchors);
            _skipCentroid = ComputeCentroid(SkipAnchors);
        }
    }

    private static float[] ComputeCentroid(string[] anchors)
    {
        const int dim = 384;
        var sum = new float[dim];
        int count = 0;

        // 优先使用 ONNX LocalEmbedder（BGE 模型），不可用时回退 FastEmb
        // P12.3: GetSharedEmbedder returns null when remote API is available
        // (LocalEmbedder.DefaultDisabled) — fall through to FastEmb without
        // ever loading the 90 MB model file.
        var localEmb = GetSharedEmbedder();

        foreach (var anchor in anchors)
        {
            float[] emb;
            if (localEmb != null && localEmb.Available)
            {
                emb = localEmb.Generate(anchor);
            }
            else
            {
                emb = LTAI.AI.EmbeddingClient.FastEmb(anchor, dim);
            }
            if (emb.Length == 0) continue;
            for (int i = 0; i < Math.Min(emb.Length, dim); i++) sum[i] += emb[i];
            count++;
        }
        if (count > 0)
            for (int i = 0; i < dim; i++) sum[i] /= count;
        return sum;
    }

    private static float CosineSimilarity(float[] a, float[] b)
        => LTAI.AI.VectorMath.CosineSimilarity(a.AsSpan(), b.AsSpan());

    /// <summary>代码模式启发式检测 — 含 C#/代码关键字则强制走 KG。委托给共享 QueryUtils。</summary>
    private static bool ContainsCodePattern(string text) => QueryUtils.ContainsCodePattern(text);

    /// <summary>Intent-based KG gate. Uses FastEmb + cosine similarity.</summary>
    internal static bool IsKnowledgeQuery(string text)
    {
        // 代码模式 → 强制走 KG（跳过 centroid 分类）
        if (ContainsCodePattern(text))
            return true;

        EnsureCentroids();
        var emb = LTAI.AI.EmbeddingClient.FastEmb(text.Trim(), 384);
        var knowledgeScore = CosineSimilarity(emb, _knowledgeCentroid!);
        var skipScore = CosineSimilarity(emb, _skipCentroid!);
        return knowledgeScore > skipScore + 0.05f;
    }

    // 共享 LocalEmbedder 实例 — 避免每次查询都加载 90MB ONNX 模型
    // P12.3: respects LocalEmbedder.DefaultDisabled (when remote API key is
    // present, returns null and callers fall back to FastEmb without ever
    // touching the local model).
    private static readonly Lazy<LocalEmbedder?> _sharedEmbedder = new(() =>
        LocalEmbedder.DefaultDisabled ? null : new LocalEmbedder(), true);

    private static LocalEmbedder? GetSharedEmbedder() => _sharedEmbedder.Value;

    private static string FormatNode(NodeRow node)
    {
        var icon = node.Kind switch
        {
            "document" => "📄", "concept" => "🏷️", "fact" => "💡",
            _ => "▪️"
        };
        return $"{icon} [{node.Kind}] {node.Name}" +
               (string.IsNullOrEmpty(node.Namespace) ? "" : $" ({node.Namespace})");
    }

    private static List<string> ExtractConcepts(string title, string content)
    {
        return (title + " " + content)
            .Split([' ', '\n', '\r', ',', '.', '(', ')', '【', '】', '：', '，', '。'],
                StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
    }
}

