// Copyright (c) LTAI. All rights reserved.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LTAI.AI;
using LTAI.Agent.Tools;
using LTAI.Agent.Utils;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Vector;

/// <summary>
/// Multi-language Code Graph (SQLite + FTS5 + CTE).
/// Pipeline: LLM rewrite → FTS5 BM25 → CTE graph expansion → context injection.
/// </summary>
public sealed class CgGraph : AIContextProvider
{
    private readonly KgStore _store;
    private readonly IChatClient? _rewriter;
    private readonly LTAI.AI.EmbeddingClient? _embedder;
    private readonly ILogger<CgGraph> _logger;
    private readonly string _ws;
    private bool _built;
    private readonly ConcurrentDictionary<string, DateTime> _indexedFiles = new(StringComparer.OrdinalIgnoreCase);
    private TreeSitterParser? _parser;

    private static readonly HashSet<string> SourceExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".py", ".js", ".jsx", ".ts", ".tsx", ".go", ".rs", ".java",
        ".sh", ".bash", ".json", ".html", ".css",
        ".mbt", ".mojo", "🔥", ".cj",
    };

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="store">SQLite KgStore.</param>
    /// <param name="rewriter">Optional LLM for query→keyword rewriting. If null, raw query is used.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="ws">Workspace root for code indexing.</param>
    public CgGraph(KgStore store, IChatClient? rewriter = null,
        LTAI.AI.EmbeddingClient? embedder = null,
        ILogger<CgGraph>? logger = null, string? ws = null)
        : base(null, null, null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _rewriter = rewriter;
        _embedder = embedder;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ws = ws ?? Directory.GetCurrentDirectory();
    }

    // ═══════════════════════════════════════════
    //  Build / Incremental index
    // ═══════════════════════════════════════════

    public async Task<string> BuildAsync(string? directory = null)
    {
        var dir = directory ?? _ws;
        if (!Directory.Exists(dir)) return "Directory not found";

        _parser ??= new TreeSitterParser(_logger);

        var files = DirectoryWalker.WalkToArray(
            dir,
            allowedExtensions: SourceExts,
            skipDirNames: new(StringComparer.OrdinalIgnoreCase)
            {
                "obj", "bin", "dist", "node_modules", ".git", "packages"
            });

        // Parallel file indexing: read + parse across CPU cores, write serialized.
        int sc = 0, na = 0;

        var maxDop = Math.Max(1, Environment.ProcessorCount / 2);
        await Parallel.ForEachAsync(files,
            new ParallelOptions { MaxDegreeOfParallelism = maxDop },
            async (file, ct) =>
        {
            var lw = File.GetLastWriteTimeUtc(file);
            if (_indexedFiles.TryGetValue(file, out var pw) && pw >= lw) return;

            var rel = Path.GetRelativePath(_ws, file).Replace('\\', '/');

            try
            {
                var fi = new FileInfo(file);
                if (fi.Length > 10 * 1024 * 1024)
                {
                    _logger.LogWarning("CgGraph: skipping large file {Rel} ({Size} MB)", rel, fi.Length / 1024 / 1024);
                    _indexedFiles[file] = lw;
                    Interlocked.Increment(ref sc);
                    return;
                }
                var code = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                var ext = Path.GetExtension(file);
                var fileName = Path.GetFileName(file);
                var lineCount = code.Split('\n').Length;

                var symbols = _parser.ExtractSymbols(code, ext);

                // Single transaction per file: all writes batched in one lock + transaction
                await _store.ExecuteInTransactionAsync(async () =>
                {
                    await _store.DeleteSource(rel).ConfigureAwait(false);
                    int fileNodeCount = 1;

                    var fid = await _store.UpsertNode(
                        extId: $"file:{rel}",
                        kind: "file",
                        name: fileName,
                        ns: rel,
                        signature: ext,
                        source: rel,
                        props: new() { ["path"] = rel, ["ext"] = ext, ["lines"] = lineCount }).ConfigureAwait(false);

                    await _store.AddDoc(fid, code, "code", rel).ConfigureAwait(false);
                    var fileEmb = _embedder != null
                        ? await _embedder.GenerateAsync($"{fileName} {rel}", ct).ConfigureAwait(false)
                        : LTAI.AI.EmbeddingClient.FastEmb($"{fileName} {rel}");
                    await _store.InsertVectorAsync(fid, fileEmb).ConfigureAwait(false);

                    // Collect method/function nodes for intra-file CALLS
                    var methodNodes = new List<(long nid, string name, int line)>();
                    foreach (var (kind, name, line, _) in symbols)
                    {
                        var safeName = name.Replace("<", "_").Replace(">", "_").Replace("(", "_").Replace(")", "_");
                        var nid = await _store.UpsertNode(
                            extId: $"{kind}:{Path.GetFileNameWithoutExtension(file)}:{safeName}",
                            kind: MapKind(kind),
                            name: safeName,
                            ns: rel,
                            signature: $"L{line}",
                            source: rel,
                            props: new() { ["file"] = rel, ["line"] = line, ["ext"] = ext }).ConfigureAwait(false);

                        await _store.AddEdge(fid, nid, "defines").ConfigureAwait(false);
                        var symEmb = _embedder != null
                            ? await _embedder.GenerateAsync($"{safeName} {kind}", ct).ConfigureAwait(false)
                            : LTAI.AI.EmbeddingClient.FastEmb($"{safeName} {kind}");
                        await _store.InsertVectorAsync(nid, symEmb).ConfigureAwait(false);

                        var ctx = GetContext(code, line);
                        await _store.AddDoc(nid, ctx, "code", $"{rel}:L{line}").ConfigureAwait(false);
                        fileNodeCount++;

                        var mappedKind = MapKind(kind);
                        if (mappedKind is "method" or "function")
                            methodNodes.Add((nid, safeName, line));
                    }

                    // Intra-file CALLS via name match
                    if (methodNodes.Count > 1)
                    {
                        foreach (var (callerId, callerName, callLine) in methodNodes)
                        {
                            var ctx = GetContext(code, callLine);
                            foreach (var (calleeId, calleeName, _) in methodNodes)
                            {
                                if (callerId == calleeId) continue;
                                if (ctx.Contains(calleeName) || ctx.Contains(calleeName + "("))
                                {
                                    await _store.AddEdge(callerId, calleeId, "CALLS", weight: 0.8).ConfigureAwait(false);
                                }
                            }
                        }
                    }

                    Interlocked.Add(ref na, fileNodeCount);
                }).ConfigureAwait(false); // transaction + lock released here

                _indexedFiles[file] = lw;
                Interlocked.Increment(ref sc);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "CgGraph: failed to index {File}", file);
            }
        }).ConfigureAwait(false);

        _built = true;

        // Post-index: cross-file CALLS inference
        await InferCrossFileCallsAsync().ConfigureAwait(false);

        // Post-index: detect deleted files and prune orphaned nodes
        await PruneDeletedFilesAsync([.. files]).ConfigureAwait(false);

        // Persist current file list for next build's diff
        await _store.SetMeta("cg:files", string.Join("\n", files)).ConfigureAwait(false);

        // Maintenance
        if (sc > 0 || _indexedFiles.Count % 10 == 0)
        {
            var (p, before, after) = await _store.RunMaintenanceAsync(_ws, TimeSpan.FromDays(30)).ConfigureAwait(false);
            _logger.LogInformation("CgGraph: GC {P} stale, {Before}B→{After}B", p, before, after);
        }

        return $"Built: {sc} files, {na} symbols\n{await _store.Stats().ConfigureAwait(false)}";
    }

    // ═══════════════════════════════════════════
    //  Query
    // ═══════════════════════════════════════════

    public async Task<string> QueryAsync(string query, int topK = 5, CancellationToken ct = default)
        => await QueryByNamespaceAsync(query, null, topK, ct).ConfigureAwait(false);

    /// <summary>
    /// Query code graph scoped to a specific namespace prefix (e.g. "LTAI.Agent").
    /// When namespace is null, searches all namespaces (same as QueryAsync).
    /// </summary>
    public async Task<string> QueryByNamespaceAsync(string query, string? namespacePrefix,
        int topK = 5, CancellationToken ct = default)
    {
        if (!_built) return "Code graph not built — run /build command first.";

        var keywords = await RewriteQueryAsync(query, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(keywords)) keywords = query;

        _logger.LogInformation("CgGraph: \"{Q}\" → keywords: \"{K}\"{Ns}", query, keywords,
            namespacePrefix != null ? $" (ns: {namespacePrefix})" : "");

        var ftsHits = await _store.SearchFts(keywords, topN: topK * 3).ConfigureAwait(false);

        // Namespace filter: when a prefix is specified, only keep hits
        // whose node namespace matches (prefix match, e.g. "LTAI.Agent" matches "LTAI.Agent.Vector")
        if (namespacePrefix != null)
        {
            var filtered = new List<(long nodeId, string text, double rank, string kind)>();
            foreach (var hit in ftsHits)
            {
                var node = await _store.GetNode(hit.nodeId).ConfigureAwait(false);
                if (node?.Namespace != null &&
                    (node.Namespace == namespacePrefix ||
                     node.Namespace.StartsWith(namespacePrefix + ".", StringComparison.OrdinalIgnoreCase)))
                {
                    filtered.Add(hit);
                }
            }
            ftsHits = filtered;
        }
        if (ftsHits.Count == 0) return "No relevant code found.";

        var seen = new HashSet<long>();
        var lines = new List<string> { "## Relevant Code:\n" };

        foreach (var hit in ftsHits.Take(topK))
        {
            if (!seen.Add(hit.nodeId)) continue;
            var node = await _store.GetNode(hit.nodeId).ConfigureAwait(false);
            if (node == null) continue;

            var icon = node.Kind switch
            {
                "class" => "📦", "method" => "🔧", "interface" => "📐",
                "file" => "📄", "enum" => "🔢", "struct" => "🏗️", _ => "▪️"
            };
            lines.Add($"{icon} **[{node.Kind}]** `{node.Name}` — {node.Namespace}");

            // 1-hop neighbors
            foreach (var edge in (await _store.GetEdges(hit.nodeId).ConfigureAwait(false)).Take(5))
            {
                var neighborId = edge.Src == hit.nodeId ? edge.Dst : edge.Src;
                if (!seen.Add(neighborId)) continue;
                var neighbor = await _store.GetNode(neighborId).ConfigureAwait(false);
                if (neighbor == null) continue;
                lines.Add($"  ══ {edge.Relation} ══ [{neighbor.Kind}] `{neighbor.Name}`");
            }

            // Doc snippet
            foreach (var doc in (await _store.GetDocs(hit.nodeId).ConfigureAwait(false)).Take(1))
            {
                var snippet = doc.Text.Length > 150 ? doc.Text[..150] + "…" : doc.Text;
                lines.Add($"  ```\n{snippet}\n```");
            }
            lines.Add("");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Discover distinct namespaces in the code graph with their node counts.
    /// Used by Expert layer to create per-namespace code graph experts.
    /// Only returns namespaces with ≥ 5 nodes (filtering noise).
    /// </summary>
    public async Task<IReadOnlyList<(string Namespace, int NodeCount)>> GetNamespacesAsync(
        CancellationToken ct = default)
    {
        if (!_built) return [];
        try
        {
            var allNodes = await _store.GetAllNodes().ConfigureAwait(false);
            return allNodes
                .Where(n => !string.IsNullOrEmpty(n.Namespace))
                .GroupBy(n => n.Namespace!)
                .Select(g => (g.Key, g.Count()))
                .Where(x => x.Item2 >= 5)
                .OrderByDescending(x => x.Item2)
                .Take(30)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    // ═══════════════════════════════════════════
    //  AIContextProvider
    // ═══════════════════════════════════════════

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext ctx, CancellationToken ct = default)
    {
        // Skip unless code index has been manually built (not auto-triggered
        // on first query, because TreeSitter native parser can crash on some files).
        if (!_built) return ctx.AIContext!;

        var msgs = ctx.AIContext?.Messages;
        if (msgs == null) return ctx.AIContext!;

        var userMsg = msgs.LastOrDefault(m => m.Role == ChatRole.User);
        if (userMsg?.Text == null || userMsg.Text.Length < 5)
            return ctx.AIContext!;

        // Skip code search for casual chat
        if (!KbGraph.IsKnowledgeQuery(userMsg.Text))
            return ctx.AIContext!;

        // Skip code graph query when ExpertRouterAgent already injected aggregated context
        foreach (var m in msgs.Reverse())
        {
            if (m.Role == ChatRole.System && m.Text?.StartsWith("## Expert Context") == true)
            {
                _logger.LogDebug("CgGraph: skipped — ExpertRouterAgent already injected context");
                return ctx.AIContext!;
            }
        }

        try
        {

            var result = await QueryAsync(userMsg.Text, topK: 3, ct: ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(result) || result.StartsWith("No relevant"))
                return ctx.AIContext!;

            _logger.LogInformation("CgGraph: injected context for: \"{Q}\"", userMsg.Text);

            return new AIContext
            {
                Instructions = ctx.AIContext?.Instructions != null
                    ? ctx.AIContext.Instructions + "\n\n" + result
                    : result,
                Messages = ctx.AIContext?.Messages,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CgGraph query failed");
            return ctx.AIContext!;
        }
    }

    // ═══════════════════════════════════════════
    //  Private
    // ═══════════════════════════════════════════

    /// <summary>
    /// L0 short-circuit (reuses KbGraph logic): simple queries don't trigger LLM.
    /// Delegates to shared QueryUtils.
    /// </summary>
    private static bool IsSimpleQuery(string query) => QueryUtils.IsSimpleQuery(query);

    private async Task<string> RewriteQueryAsync(string query, CancellationToken ct)
    {
        // L0 短路
        if (_rewriter == null || IsSimpleQuery(query)) return query;
        try
        {
            var prompt = $"""
                You are a code search assistant. Convert the following query into
                3-8 keywords (class names, method names, file names, error terms, etc.).
                Return ONLY space-separated keywords.
                Query: {query}
                Keywords:
                """;
            var resp = await _rewriter.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct).ConfigureAwait(false);
            return resp.Text?.Trim() ?? query;
        }
        catch { return query; }
    }

    private static string MapKind(string tsKind) => tsKind.ToLowerInvariant() switch
    {
        "class" => "class",
        "method" or "function" or "method_declaration" => "method",
        "interface" => "interface",
        "enum" => "enum",
        "property" or "variable" => "property",
        "struct" => "struct",
        "record" => "record",
        _ => tsKind.ToLowerInvariant().Replace("_declaration", "").Replace("_definition", "")
    };

    private static string GetContext(string code, int lineNum)
    {
        var lines = code.Split('\n');
        var start = Math.Max(0, lineNum - 3);
        var end = Math.Min(lines.Length, lineNum + 2);
        return string.Join("\n", lines[start..end]);
    }

    // ═══════════════════════════════════════════
    //  Post-index: cross-file CALLS inference
    // ═══════════════════════════════════════════

    /// <summary>
    /// After indexing all files, scan method docs for names of other methods
    /// across the entire codebase and create CALLS edges.
    /// </summary>
    private async Task InferCrossFileCallsAsync()
    {
        try
        {
            var methods = await _store.GetNodesByKind("method").ConfigureAwait(false);
            if (methods.Count < 2) return;

            _logger.LogInformation("CgGraph: inferring cross-file CALLS for {N} methods", methods.Count);

            var nameIndex = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in methods)
            {
                if (!nameIndex.ContainsKey(m.Name))
                    nameIndex[m.Name] = new List<long>();
                nameIndex[m.Name].Add(m.Id);
            }

            const int MaxCalleesPerCaller = 20;
            const int MaxDocScanChars = 1000;

            await _store.ExecuteInTransactionAsync(async () =>
            {
                int added = 0;
                foreach (var caller in methods)
                {
                    var docs = await _store.GetDocs(caller.Id).ConfigureAwait(false);
                    var docText = string.Join("\n", docs.Select(d => d.Text));
                    if (string.IsNullOrWhiteSpace(docText)) continue;
                    if (docText.Length > MaxDocScanChars)
                        docText = docText[..MaxDocScanChars];

                    var seen = new HashSet<long> { caller.Id };
                    int calleeCount = 0;
                    foreach (var (calleeName, calleeIds) in nameIndex)
                    {
                        if (calleeName == caller.Name) continue;
                        if (!docText.Contains(calleeName) && !docText.Contains(calleeName + "(")) continue;

                        foreach (var calleeId in calleeIds)
                        {
                            if (!seen.Add(calleeId)) continue;
                            await _store.AddEdge(caller.Id, calleeId, "CALLS", weight: 0.6).ConfigureAwait(false);
                            added++;
                            calleeCount++;
                            if (calleeCount >= MaxCalleesPerCaller) break;
                        }
                        if (calleeCount >= MaxCalleesPerCaller) break;
                    }
                }
                _logger.LogInformation("CgGraph: added {N} cross-file CALLS edges", added);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CgGraph: cross-file CALLS inference failed");
        }
    }

    // ═══════════════════════════════════════════
    //  Post-index: orphaned file pruning
    // ═══════════════════════════════════════════

    /// <summary>
    /// Prune nodes whose source file was deleted between builds.
    /// Uses Meta("cg:files") to track the known set across runs.
    /// </summary>
    private async Task PruneDeletedFilesAsync(List<string> currentFiles)
    {
        try
        {
            var prevRaw = await _store.GetMeta("cg:files").ConfigureAwait(false);
            if (string.IsNullOrEmpty(prevRaw)) return;

            var prevSet = new HashSet<string>(
                prevRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);

            var currentSet = new HashSet<string>(currentFiles, StringComparer.OrdinalIgnoreCase);

            // Files in prevSet but not in currentSet were deleted
            foreach (var missing in prevSet)
            {
                if (currentSet.Contains(missing)) continue;
                var rel = Path.GetRelativePath(_ws, missing).Replace('\\', '/');
                _logger.LogInformation("CgGraph: pruning deleted file \"{Rel}\"", rel);
                await _store.DeleteSource(rel).ConfigureAwait(false);
                _indexedFiles.TryRemove(missing, out _);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CgGraph: prune deleted files failed");
        }
    }

    public void Dispose() => _parser?.Dispose();
}
