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

    public DocumentIndexer(KgStore kg, KnowledgeExtractor extractor, ILogger<DocumentIndexer> logger)
    {
        _kg = kg;
        _extractor = extractor;
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
                // Layer 1: Path screen
                var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                var pathVerdict = ContentFilter.ScreenPath(rel);
                if (pathVerdict != FilterVerdict.Allowed)
                {
                    Interlocked.Increment(ref skipped);
                    _logger.LogTrace("IndexDirectory: skipped '{Path}' ({Verdict})", rel, pathVerdict);
                    return;
                }

                var content = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);

                // Layer 2: Content screen
                var contentVerdict = ContentFilter.ScreenContent(content, rel);
                if (contentVerdict != FilterVerdict.Allowed)
                {
                    Interlocked.Increment(ref skipped);
                    _logger.LogTrace("IndexDirectory: skipped '{Path}' ({Verdict})", rel, contentVerdict);
                    return;
                }

                if (string.IsNullOrWhiteSpace(content)) return;

                var title = Path.GetFileNameWithoutExtension(file);
                var chunks = SemanticChunker.Chunk(content);

                foreach (var chunk in chunks)
                {
                    var extId = $"{rel}#{chunk.GetHashCode():x}";
                    await _kg.UpsertNode(extId, "document", title,
                        source: rel, props: new() { ["text"] = chunk }).ConfigureAwait(false);
                }

                _logger.LogDebug("Indexed {File}: {Chunks} chunks", rel, chunks.Count);
                Interlocked.Increment(ref ok);
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

            foreach (var chunk in chunks)
            {
                var extId = $"{rel}#{chunk.GetHashCode():x}";
                await _kg.UpsertNode(extId, "document", title,
                    source: rel, props: new() { ["text"] = chunk }).ConfigureAwait(false);
            }

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
