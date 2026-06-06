// Copyright (c) LTAI. All rights reserved.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LTAI.Core.Storage;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Workflows;

/// <summary>
/// P15 FileSystemWatcher-based hot-reload trigger. Watches the workflow
/// directory (default <c>.livingtree/workflows/</c>) and emits events when
/// YAML/JSON files change. The <see cref="YAMLWorkflowRegistry"/> subscribes
/// to reload changed files; D68 keeps the old workflow on failure.
/// </summary>
/// <remarks>
/// <para><b>Debouncing:</b> editors often emit 2-3 events per save
/// (Created + Changed + sometimes Renamed). We debounce with a 250ms timer
/// per file path so the registry reloads once per logical save.</para>
/// <para><b>Watched extensions:</b> <c>*.yaml</c> (MAF declarative
/// workflows) and <c>*.json</c> (LTAI config). Other files are ignored.</para>
/// <para><b>Directory creation:</b> if the watched directory does not exist
/// at startup, the watcher creates it (so the user can drop files in
/// immediately and they'll be picked up).</para>
/// </remarks>
public sealed class YAMLWorkflowWatcher : IDisposable
{
    private readonly string _watchDir;
    private readonly YAMLWorkflowRegistry _registry;
    private readonly ILogger<YAMLWorkflowWatcher> _logger;
    private FileSystemWatcher? _fsWatcher;
    private readonly ConcurrentDictionary<string, Timer> _debounceTimers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(250);
    private bool _disposed;

    public YAMLWorkflowWatcher(
        string watchDir,
        YAMLWorkflowRegistry registry,
        ILogger<YAMLWorkflowWatcher> logger)
    {
        _watchDir = watchDir;
        _registry = registry;
        _logger = logger;
    }

    /// <summary>
    /// Begin watching the directory. Idempotent — calling twice is a no-op.
    /// Creates the directory if it does not exist.
    /// </summary>
    public void Start()
    {
        if (_fsWatcher != null) return;
        if (!Directory.Exists(_watchDir))
        {
            Directory.CreateDirectory(_watchDir);
            _logger.LogInformation("Created workflow directory: {Dir}", _watchDir);
        }

        _fsWatcher = new FileSystemWatcher(_watchDir)
        {
            IncludeSubdirectories = false,
            InternalBufferSize = 65536,
            NotifyFilter = NotifyFilters.FileName
                         | NotifyFilters.LastWrite
                         | NotifyFilters.Size
                         | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        _fsWatcher.Created += OnChanged;
        _fsWatcher.Changed += OnChanged;
        _fsWatcher.Renamed += OnRenamed;
        _fsWatcher.Error += OnError;

        _logger.LogInformation("Workflow watcher started: {Dir} (*.yaml, *.json)", _watchDir);
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => DebounceReload(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e) => DebounceReload(e.FullPath);

    private void OnError(object sender, ErrorEventArgs e)
    {
        _logger.LogError(e.GetException(), "FileSystemWatcher error in {Dir}", _watchDir);
        // Restart watcher after buffer overflow
        try
        {
            _fsWatcher.EnableRaisingEvents = false;
            _fsWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart FileSystemWatcher in {Dir}", _watchDir);
        }
    }

    private void DebounceReload(string path)
    {
        if (!IsWatchedFile(path)) return;
        // Coalesce multiple events for the same file into a single reload
        // (editors often emit Created+Changed+Changed in quick succession).
        // A1: TryRemove + fresh TryAdd avoids ObjectDisposedException from
        // calling Change() on a Timer that has already fired and disposed itself.
        _debounceTimers.TryRemove(path, out var old);
        old?.Dispose();
        _debounceTimers.TryAdd(
            path, new Timer(_ => _ = ReloadAsync(path), null, DebounceDelay, Timeout.InfiniteTimeSpan));
    }

    private static bool IsWatchedFile(string path)
    {
        var ext = Path.GetExtension(path);
        return string.Equals(ext, ".yaml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".yml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ReloadAsync(string path)
    {
        try
        {
            // File may still be locked by the editor; retry up to 3 times with
            // exponential backoff (50ms, 100ms, 200ms).
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        await _registry.ReloadFileAsync(path).ConfigureAwait(false);

                        // P6: auto-commit the changed file to LocalVersionRepo
                        TryCommitToRepo(path);

                        return;
                    }
                }
                catch (IOException) when (attempt < 2)
                {
                    await Task.Delay(50 << attempt).ConfigureAwait(false);
                    continue;
                }
            }
            _logger.LogWarning("Reload skipped: file still locked after 3 attempts: {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error reloading {Path}", path);
        }
    }

    private static void TryCommitToRepo(string path)
    {
        try
        {
            var baseDir = LocalVersionRepo.BaseDirectory;
            if (!path.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                return;
            var rel = Path.GetRelativePath(baseDir, path);
            LocalVersionRepo.Commit(rel, $"♻ Hot-reload: {Path.GetFileName(path)}");
        }
        catch
        {
            // best-effort: commit failure should not break the reload flow
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_fsWatcher != null)
        {
            _fsWatcher.EnableRaisingEvents = false;
            _fsWatcher.Dispose();
            _fsWatcher = null;
        }
        foreach (var timer in _debounceTimers.Values) timer.Dispose();
        _debounceTimers.Clear();
        _logger.LogDebug("Workflow watcher stopped: {Dir}", _watchDir);
    }
}
