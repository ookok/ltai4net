using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Vector;

public sealed class GraphInitService : IHostedService, IDisposable
{
    private readonly CgGraph _cg;
    private readonly KbGraph _kb;
    private readonly ILogger<GraphInitService> _logger;
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private string? _debouncePath;
    private readonly object _debounceLock = new();
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(500);
    private bool _building;

    public GraphInitService(CgGraph cg, KbGraph kb, ILogger<GraphInitService> logger)
    {
        _cg = cg;
        _kb = kb;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("Graph: scheduling initial build in 10s...");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                await BuildAllAsync(ct).ConfigureAwait(false);
                StartWatcher();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Graph: background init failed");
            }
        }, ct);
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

    public async Task BuildAllAsync(CancellationToken ct = default)
    {
        if (_building) return;
        _building = true;
        try
        {
            _logger.LogInformation("Graph: building code index...");
            var codeResult = await _cg.BuildAsync().ConfigureAwait(false);
            _logger.LogInformation("Graph: {Result}", codeResult.Replace("\n", " | "));

            _logger.LogInformation("Graph: building document index...");
            var docResult = await _kb.BuildDocumentIndexAsync(Directory.GetCurrentDirectory()).ConfigureAwait(false);
            _logger.LogInformation("Graph: {Result}", docResult);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Graph: init failed");
        }
        finally
        {
            _building = false;
        }
    }

    private void StartWatcher()
    {
        var ws = Directory.GetCurrentDirectory();
        if (!Directory.Exists(ws)) return;

        _watcher = new FileSystemWatcher(ws)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 65536,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
        };
        // Only watch source files; exclude build artifacts by default
        _watcher.Filters.Clear();
        foreach (var ext in new[] { "*.cs", "*.js", "*.ts", "*.tsx", "*.rs", "*.go", "*.java", "*.sh", "*.bash" })
            _watcher.Filters.Add(ext);

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Deleted += OnFileDeleted;
        _watcher.Renamed += OnFileRenamed;
        _watcher.Error += (_, e) =>
        {
            _logger.LogError(e.GetException(), "Graph file watcher error");
            try { _watcher.EnableRaisingEvents = false; _watcher.EnableRaisingEvents = true; } catch { }
        };
        _watcher.EnableRaisingEvents = true;
        _logger.LogInformation("Graph: file watcher started on {Dir}", ws);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (ShouldSkip(e.FullPath)) return;
        ScheduleRebuild(e.FullPath);
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        if (ShouldSkip(e.FullPath)) return;
        ScheduleRebuild(e.FullPath);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (ShouldSkip(e.FullPath) && ShouldSkip(e.OldFullPath)) return;
        ScheduleRebuild(e.FullPath);
    }

    private static bool ShouldSkip(string path)
    {
        if (string.IsNullOrEmpty(path)) return true;
        var sep = Path.DirectorySeparatorChar;
        var p = path.AsSpan();
        return p.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase)
            || p.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase)
            || p.Contains($"{sep}dist{sep}", StringComparison.OrdinalIgnoreCase)
            || p.Contains($"{sep}node_modules{sep}", StringComparison.OrdinalIgnoreCase)
            || p.Contains($"{sep}.git{sep}", StringComparison.OrdinalIgnoreCase)
            || p.Contains($"{sep}packages{sep}", StringComparison.OrdinalIgnoreCase);
    }

    private void ScheduleRebuild(string path)
    {
        lock (_debounceLock)
        {
            _debouncePath = path;
            _debounceTimer?.Change(DebounceInterval, Timeout.InfiniteTimeSpan);
            _debounceTimer ??= new Timer(_ =>
            {
                string? p;
                lock (_debounceLock) { p = _debouncePath; _debouncePath = null; }
                if (p != null)
                {
                    _logger.LogInformation("Graph: file changed ({Rel}), incremental update...", Path.GetRelativePath(Directory.GetCurrentDirectory(), p));
                    _ = BuildAllAsync();
                }
            }, null, DebounceInterval, Timeout.InfiniteTimeSpan);
        }
    }
}
