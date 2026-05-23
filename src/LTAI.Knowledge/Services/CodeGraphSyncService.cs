using LTAI.Tools.CodeGraph;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.Knowledge.Services;

public sealed class CodeGraphSyncService : BackgroundService
{
    private readonly CodeGraph _codeGraph;
    private readonly CodeGraphEnhanced _codeGraphEnhanced;
    private readonly ILogger<CodeGraphSyncService> _logger;
    private readonly TimeSpan _indexInterval;
    private readonly string _watchPath;
    private FileSystemWatcher? _watcher;
    private DateTime _lastGitSync = DateTime.MinValue;
    private static readonly TimeSpan GitSyncCooldown = TimeSpan.FromMinutes(15);

    public CodeGraphSyncService(
        CodeGraph codeGraph,
        CodeGraphEnhanced codeGraphEnhanced,
        ILogger<CodeGraphSyncService> logger,
        string? watchPath = null)
    {
        _codeGraph = codeGraph;
        _codeGraphEnhanced = codeGraphEnhanced;
        _logger = logger;
        _watchPath = watchPath ?? Directory.GetCurrentDirectory();
        _indexInterval = TimeSpan.FromMinutes(30);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("CodeGraphSyncService: Started, watchPath={Path}", _watchPath);

        await Task.Delay(TimeSpan.FromSeconds(10), ct);
        await FullIndexAsync(ct);

        SetupFileWatcher(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_indexInterval, ct);
                await IncrementalUpdateAsync();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "CodeGraphSyncService: Periodic update failed"); }
        }

        _watcher?.Dispose();
    }

    private async Task FullIndexAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("CodeGraphSyncService: Full indexing started");
            await _codeGraph.IndexAsync();
            var hubs = _codeGraph.FindHubs(5);
            _logger.LogInformation("CodeGraphSyncService: Simple graph indexed, hubs={Hubs}", hubs.Count);

            await _codeGraphEnhanced.IndexAsync();
            _logger.LogInformation("CodeGraphSyncService: Enhanced graph indexed");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CodeGraphSyncService: Full index failed");
        }
    }

    private async Task IncrementalUpdateAsync()
    {
        try
        {
            if (DateTime.UtcNow - _lastGitSync > GitSyncCooldown)
            {
                var gitDir = Path.GetDirectoryName(_watchPath);
                if (Directory.Exists(Path.Combine(gitDir ?? _watchPath, ".git")))
                {
                    await _codeGraph.IncrementalUpdateFromGitAsync();
                    _lastGitSync = DateTime.UtcNow;
                    _logger.LogDebug("CodeGraphSyncService: Git incremental update done");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CodeGraphSyncService: Incremental update skipped");
        }
    }

    private void SetupFileWatcher(CancellationToken ct)
    {
        if (!Directory.Exists(_watchPath)) return;

        try
        {
            _watcher = new FileSystemWatcher(_watchPath, "*.cs")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };

            _watcher.Changed += (_, e) => DebouncedReindex();
            _watcher.Created += (_, e) => DebouncedReindex();
            _watcher.Renamed += (_, e) => DebouncedReindex();

            ct.Register(() => _watcher?.Dispose());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CodeGraphSyncService: File watcher setup failed");
        }
    }

    private DateTime _lastFileChange = DateTime.MinValue;
    private async void DebouncedReindex()
    {
        _lastFileChange = DateTime.UtcNow;
        await Task.Delay(3000);
        if ((DateTime.UtcNow - _lastFileChange).TotalSeconds < 2) return;

        try
        {
            await _codeGraph.IndexAsync();
            _logger.LogDebug("CodeGraphSyncService: Re-indexed after file change");
        }
        catch (Exception ex) { _logger.LogDebug(ex, "CodeGraphSyncService: Debounced reindex failed"); }
    }
}
