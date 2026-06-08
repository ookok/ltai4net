// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  IndexQueueWorker — queues file indexing work to TaskQueue
//
//  Problem: IndexDirectoryAsync runs Parallel.ForEach which can
//  overwhelm KgStore's SQLite writer. Thousands of concurrent
//  UpsertNode calls contend for the same write lock, causing
//  WAL-log bloat and slowdown.
//
//  Solution: Enqueue each file as a TaskQueue work item.
//  TaskQueue's channel-based consumer loop runs at
//  maxConcurrency (default 4), naturally throttling
//  KgStore writes without oversubscribing.
// ═══════════════════════════════════════════════════════════════

using LTAI.Agent.Vector;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Indexing;

/// <summary>
/// Wraps DocumentIndexer behind the TaskQueue. IndexDirectoryAsync
/// enqueues each file's indexing work to the queue, where it's
/// consumed at a controlled concurrency rate.
/// </summary>
public sealed class IndexQueueWorker
{
    private readonly DocumentIndexer _indexer;
    private readonly Tasks.TaskQueue _queue;
    private readonly ILogger<IndexQueueWorker> _logger;

    public IndexQueueWorker(
        DocumentIndexer indexer,
        Tasks.TaskQueue queue,
        ILogger<IndexQueueWorker> logger)
    {
        _indexer = indexer ?? throw new ArgumentNullException(nameof(indexer));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Index a directory via the task queue. Returns immediately with enqueued count.
    /// Use <see cref="Tasks.TaskQueue.WaitAsync"/> to wait for individual items.
    /// </summary>
    public async Task<int> IndexDirectoryAsync(
        string dir,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(dir))
        {
            _logger.LogWarning("IndexQueueWorker: directory not found '{Dir}'", dir);
            return 0;
        }

        var files = Utils.DirectoryWalker.WalkToArray(
            dir,
            allowedExtensions: ContentFilter.GetIndexerExtensions(),
            skipDirNames: DocumentIndexer.DefaultSkipDirNames);

        var enqueued = 0;
        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;

            // Path screening at enqueue time (cheap)
            var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
            if (ContentFilter.ScreenPath(rel) != FilterVerdict.Allowed)
                continue;

            var item = await _queue.EnqueueAsync(
                name: $"index:{rel}",
                work: async (innerCt) =>
                {
                    var result = await _indexer.IndexFileAsync(file, rel, innerCt)
                        .ConfigureAwait(false);
                    return result.ToString();
                },
                description: rel,
                ct: ct).ConfigureAwait(false);

            enqueued++;
        }

        _logger.LogInformation("IndexQueueWorker: enqueued {Count} files from '{Dir}'", enqueued, dir);
        return enqueued;
    }

    /// <summary>
    /// Index a single file via the task queue. Returns the task item for polling.
    /// </summary>
    public async Task<Tasks.TaskItem> IndexFileAsync(
        string filePath,
        string? source = null,
        CancellationToken ct = default)
    {
        var rel = source ?? Path.GetFileName(filePath);
        return await _queue.EnqueueAsync(
            name: $"index:{rel}",
            work: async (innerCt) =>
            {
                var result = await _indexer.IndexFileAsync(filePath, rel, innerCt)
                    .ConfigureAwait(false);
                return result.ToString();
            },
            description: rel,
            ct: ct).ConfigureAwait(false);
    }
}
