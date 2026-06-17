// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════
//  ContractWatcher — fsnotify-based incremental contract
//  scanning. Re-scans changed files for API contract
//  declarations and updates the ContractRegistry.
// ═══════════════════════════════════════════════════════

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Vector;

public sealed class ContractWatcher : IHostedService, IDisposable
{
    private readonly ContractRegistry _registry;
    private readonly ILogger<ContractWatcher> _logger;
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private readonly HashSet<string> _pendingFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(1500);

    private static readonly HashSet<string> WatchExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".py", ".js", ".ts", ".go", ".rs", ".java",
        ".proto", ".yaml", ".yml", ".json",
    };

    public ContractWatcher(ContractRegistry registry, ILogger<ContractWatcher> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        try
        {
            var ws = Directory.GetCurrentDirectory();
            _watcher = new FileSystemWatcher(ws)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                InternalBufferSize = 65536,
            };

            _watcher.Created += OnChanged;
            _watcher.Changed += OnChanged;
            _watcher.Renamed += OnRenamed;
            _watcher.Deleted += OnDeleted;
            _watcher.Error += OnError;

            _watcher.EnableRaisingEvents = true;
            _logger.LogInformation("ContractWatcher: watching {Dir} for contract changes", ws);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ContractWatcher: failed to start");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _debounceTimer?.Dispose();
        _watcher?.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _debounceTimer?.Dispose();
        _watcher?.Dispose();
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (!WatchExtensions.Contains(Path.GetExtension(e.Name ?? ""))) return;
        if (e.Name?.Contains("\\obj\\") == true || e.Name?.Contains("\\bin\\") == true ||
            e.Name?.Contains("\\node_modules\\") == true || e.Name?.Contains("\\.git\\") == true)
            return;

        lock (_lock) _pendingFiles.Add(e.FullPath);
        _debounceTimer?.Dispose();
        _debounceTimer = new Timer(_ => FlushAsync().ConfigureAwait(false), null,
            DebounceInterval, Timeout.InfiniteTimeSpan);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        OnChanged(sender, e);
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        // Removed files: contracts from this file are stale but we
        // don't remove them (conservative — re-scan on next full build).
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        _logger.LogWarning(e.GetException(), "ContractWatcher: file watcher error");
    }

    private async Task FlushAsync()
    {
        List<string> files;
        lock (_lock)
        {
            files = [.. _pendingFiles];
            _pendingFiles.Clear();
        }

        foreach (var file in files)
        {
            try
            {
                var content = await File.ReadAllTextAsync(file).ConfigureAwait(false);
                if (content.Length > 100_000) continue;
                _registry.ScanFile(Path.GetFileName(Path.GetDirectoryName(file) ?? ""), file, content);
            }
            catch
            {
                // skip unreadable files
            }
        }

        if (files.Count > 0)
            _logger.LogDebug("ContractWatcher: scanned {N} changed files", files.Count);
    }
}
