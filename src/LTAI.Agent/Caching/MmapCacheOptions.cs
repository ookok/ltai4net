namespace LTAI.Agent.Caching;

public sealed class MmapCacheOptions
{
    public int MinReadsForCache { get; init; } = 5;
    public long MaxFileSize { get; init; } = 5 * 1024 * 1024;
    public int MaxCachedFiles { get; init; } = 500;
    public long MaxTotalBytes { get; init; } = 256 * 1024 * 1024;

    /// <summary>Directories to auto-watch for file changes. If null, no watcher runs.</summary>
    public string[]? WatchDirectories { get; init; }

    /// <summary>Debounce window (ms) to avoid repeated invalidation on rapid edits.</summary>
    public int WatchDebounceMs { get; init; } = 500;
}
