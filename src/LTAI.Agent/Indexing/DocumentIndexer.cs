// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  DocumentIndexer — file system → KgStore ingestion
//
//  UPDATED: Integrates ContentFilter at every ingestion point.
//  - IndexDirectoryAsync: path + content screening
//  - IndexFileAsync: content screening
//  - Removed ".log" from allowed extensions
// ═══════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using LTAI.Agent.Vector;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Indexing;

public sealed class DocumentIndexer
{
    private readonly KgStore _kg;
    private readonly KnowledgeExtractor _extractor;
    private readonly DocumentPageAnnotator? _annotator;
    private readonly ILogger<DocumentIndexer> _logger;

    /// <summary>
    /// Allowed text extensions for knowledge graph indexing.
    /// Explicitly excludes .log and other noise-prone extensions.
    /// Uses ContentFilter.GetIndexerExtensions() for the canonical list.
    /// </summary>
    private static readonly HashSet<string> TextExts = ContentFilter.GetIndexerExtensions();

    /// <summary>
    /// Default skip dir names, exposed for use by IndexQueueWorker.
    /// </summary>
    public static readonly HashSet<string> DefaultSkipDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "dist", "build", "out", "target", "cmake-build-debug",
        "cmake-build-release", ".next", ".nuxt", ".output",
        "node_modules", "packages", "vendor", ".venv", "venv", "__pycache__",
        "bower_components", "jspm_packages",
        ".git", ".svn", ".hg",
        ".vs", ".vscode", ".idea", ".eclipse",
        "logs", "log", "tmp", "temp", "coverage", ".nyc_output",
        ".livingtree", ".sandbox",
        "aot",
    };

    /// <summary>
    /// User-configured skip dirs (defaults from ContentFilter + any extras).
    /// </summary>
    internal static readonly HashSet<string> SkipDirNames = new(DefaultSkipDirNames, StringComparer.OrdinalIgnoreCase)
    {
        // Build / cache
        "bin", "obj", "dist", "build", "out", "target", "cmake-build-debug",
        "cmake-build-release", ".next", ".nuxt", ".output",
        // Dependencies
        "node_modules", "packages", "vendor", ".venv", "venv", "__pycache__",
        "bower_components", "jspm_packages",
        // Version control
        ".git", ".svn", ".hg",
        // IDE
        ".vs", ".vscode", ".idea", ".eclipse",
        // Logs & output — CRITICAL: prevents log file pollution
        "logs", "log", "tmp", "temp", "coverage", ".nyc_output",
        // LLM / temp
        ".livingtree", ".sandbox",
        // AOT compilation artifacts
        "aot",
    };

    public DocumentIndexer(KgStore kg, KnowledgeExtractor extractor,
        ILogger<DocumentIndexer> logger,
        DocumentPageAnnotator? annotator = null)
    {
        _kg = kg;
        _extractor = extractor;
        _annotator = annotator;
        _logger = logger;
    }

    public async Task<IndexResult> IndexDirectoryAsync(
        string dir,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(dir))
            return new IndexResult(0, 0, $"Directory not found: {dir}");

        var files = Utils.DirectoryWalker.WalkToArray(
            dir,
            allowedExtensions: TextExts,
            skipDirNames: SkipDirNames);

        int ok = 0, fail = 0, skipped = 0;
        var errors = new ConcurrentBag<string>();

        await Parallel.ForEachAsync(files, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
            CancellationToken = ct
        }, async (file, _) =>
        {
            try
            {
                var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                var result = await IndexFileAsync(file, rel, ct).ConfigureAwait(false);
                if (result.Success)
                {
                    if (result.Ok > 0) Interlocked.Add(ref ok, result.Ok);
                }
                else if (result.Error?.StartsWith("Skipped") == true)
                    Interlocked.Increment(ref skipped);
                else
                {
                    errors.Add($"{file}: {result.Error}");
                    Interlocked.Increment(ref fail);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to index {File}", file);
                errors.Add($"{file}: {ex.Message}");
                Interlocked.Increment(ref fail);
            }
        }).ConfigureAwait(false);

        if (skipped > 0)
            _logger.LogInformation("IndexDirectory: {Ok} indexed, {Fail} failed, {Skipped} filtered by ContentFilter",
                ok, fail, skipped);

        return new IndexResult(ok, fail,
            string.Join("\n", errors.Take(5)));
    }

    public async Task<IndexResult> IndexFileAsync(
        string filePath,
        string? source = null,
        CancellationToken ct = default)
    {
        try
        {
            // Layer 1: Path screen
            var rel = source ?? Path.GetFileName(filePath);
            var pathVerdict = ContentFilter.ScreenPath(rel);
            if (pathVerdict != FilterVerdict.Allowed)
            {
                _logger.LogInformation("IndexFile: skipped '{Path}' ({Verdict})", rel, pathVerdict);
                return new IndexResult(0, 0, $"Skipped: {pathVerdict}");
            }

            var content = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);

            // Layer 2: Content screen
            var contentVerdict = ContentFilter.ScreenContent(content, rel);
            if (contentVerdict != FilterVerdict.Allowed)
            {
                _logger.LogInformation("IndexFile: skipped '{Path}' ({Verdict})", rel, contentVerdict);
                return new IndexResult(0, 0, $"Skipped: {contentVerdict}");
            }

            if (string.IsNullOrWhiteSpace(content))
                return new IndexResult(0, 1, "Empty file");

            var title = Path.GetFileNameWithoutExtension(filePath);
            var chunks = SemanticChunker.Chunk(content);
            var docId = $"{rel}/gist";

            // SlideAgent-inspired: generate document-level gist (global agent)
            var gist = _annotator != null
                ? await _annotator.GenerateGistAsync(title, content, ct).ConfigureAwait(false)
                : null;

            // Store gist node
            if (gist != null)
            {
                await _kg.UpsertNode(docId, "document_gist", title,
                    source: rel, props: new() { ["gist"] = gist, ["chunk_count"] = chunks.Count }).ConfigureAwait(false);
            }

            long? gistNodeId = null;
            var gistNode = await _kg.GetNodeByExtId(docId).ConfigureAwait(false);
            if (gistNode != null) gistNodeId = gistNode.Id;

            var chunkIds = new List<long>();
            foreach (var chunk in chunks)
            {
                var extId = $"{rel}#{chunk.GetHashCode():x}";
                // SlideAgent-inspired: classify chunk semantic role (element agent)
                var role = _annotator?.ClassifyChunk(chunk) ?? "chunk";
                var props = new Dictionary<string, object?> { ["text"] = chunk, ["role"] = role };
                if (gist != null) props["gist"] = gist;

                var nodeId = await _kg.UpsertNode(extId, "document", title,
                    source: rel, props: props).ConfigureAwait(false);
                chunkIds.Add(nodeId);

                // Edge: chunk belongs to document gist
                if (gistNodeId.HasValue)
                    await _kg.AddEdge(gistNodeId.Value, nodeId, "contains", 0.8).ConfigureAwait(false);
            }

            _logger.LogDebug("Indexed {File}: {Chunks} chunks (gist={HasGist}, roles annotated)",
                rel, chunks.Count, gist != null);
            return new IndexResult(chunks.Count, 0, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to index file {File}", filePath);
            return new IndexResult(0, 1, ex.Message);
        }
    }
}

public sealed record IndexResult(int Ok, int Fail, string? Error)
{
    public bool Success => Error == null;
    public override string ToString() => Error ?? $"Indexed {Ok} chunks ({Fail} failed)";
}
