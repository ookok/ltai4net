using System.Collections.Concurrent;
using System.Threading.Tasks;
using LTAI.Agent.Vector;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Indexing;

public sealed class DocumentIndexer
{
    private readonly KgStore _kg;
    private readonly KnowledgeExtractor _extractor;
    private readonly ILogger<DocumentIndexer> _logger;

    private static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".json", ".yaml", ".yml", ".xml", ".html", ".htm",
        ".csv", ".ini", ".cfg", ".conf", ".env", ".log"
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
            skipDirNames: new(StringComparer.OrdinalIgnoreCase)
            {
                "obj", "bin", "dist", "node_modules", ".git", "packages"
            });

        int ok = 0, fail = 0;
        var errors = new ConcurrentBag<string>();

        await Parallel.ForEachAsync(files, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
            CancellationToken = ct
        }, async (file, _) =>
        {
            try
            {
                var content = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(content)) return;

                var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
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
            var content = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
                return new IndexResult(0, 1, "Empty file");

            var rel = source ?? Path.GetFileName(filePath);
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
