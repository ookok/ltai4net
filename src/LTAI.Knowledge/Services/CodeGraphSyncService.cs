using LTAI.Tools.CodeGraph;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.Knowledge.Services;

public sealed class CodeGraphSyncService : BackgroundService
{
    private readonly CodeGraphEnhanced _codeGraph;
    private readonly CodeGraphKnowledgeBridge? _bridge;
    private readonly ILogger<CodeGraphSyncService> _logger;
    private readonly TimeSpan _indexInterval;
    private readonly string _watchPath;
    private FileSystemWatcher? _watcher;

    public CodeGraphSyncService(
        CodeGraphEnhanced codeGraph,
        ILogger<CodeGraphSyncService> logger,
        CodeGraphKnowledgeBridge? bridge = null,
        string? watchPath = null)
    {
        _codeGraph = codeGraph;
        _bridge = bridge;
        _logger = logger;
        _watchPath = watchPath ?? Directory.GetCurrentDirectory();
        _indexInterval = TimeSpan.FromMinutes(30);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("CodeGraphSyncService: Started, watchPath={Path}", _watchPath);

        await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        await FullIndexAsync(ct).ConfigureAwait(false);

        SetupFileWatcher(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_indexInterval, ct).ConfigureAwait(false);
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
            await _codeGraph.IndexAsync().ConfigureAwait(false);
            var status = _codeGraph.GetStatus();
            _logger.LogInformation("CodeGraphSyncService: Graph indexed, nodes={Nodes} files={Files}",
                status.GetValueOrDefault("total_nodes"), status.GetValueOrDefault("files_indexed"));

            _bridge?.SyncToKnowledgeGraph();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CodeGraphSyncService: Full index failed");
        }
    }

    private void SetupFileWatcher(CancellationToken ct)
    {
        if (!Directory.Exists(_watchPath)) return;

        try
        {
            var extensions = new[] { "*.cs", "*.ts", "*.tsx", "*.js", "*.jsx", "*.py", "*.rs", "*.go", "*.java", "*.cpp", "*.c", "*.h", "*.fs", "*.fsx" };
            _watcher = new FileSystemWatcher(_watchPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            foreach (var ext in extensions) _watcher.Filters.Add(ext);

            _watcher.Changed += (_, _) => { try { _ = DebouncedReindexAsync(); } catch { /* best-effort */ } };
            _watcher.Created += (_, _) => { try { _ = DebouncedReindexAsync(); } catch { /* best-effort */ } };
            _watcher.Renamed += (_, _) => { try { _ = DebouncedReindexAsync(); } catch { /* best-effort */ } };

            ct.Register(() => _watcher?.Dispose());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CodeGraphSyncService: File watcher setup failed");
        }
    }

    private DateTime _lastFileChange = DateTime.MinValue;

    /// <summary>
    /// Debounced re-index triggered by file system changes.
    /// Waits 3s for quiescence, then re-indexes. If another change occurs
    /// within 2s of the delay completing, the index is skipped (stale guard).
    /// ⚠️ Called from FileSystemWatcher event handlers — exceptions are caught
    /// at the caller site to prevent process crashes from async void.
    /// </summary>
    private async Task DebouncedReindexAsync()
    {
        try
        {
            _lastFileChange = DateTime.UtcNow;
            await Task.Delay(3000).ConfigureAwait(false);
            if ((DateTime.UtcNow - _lastFileChange).TotalSeconds < 2) return;

            await _codeGraph.IndexAsync().ConfigureAwait(false);
            _logger.LogDebug("CodeGraphSyncService: Re-indexed after file change");
        }
        catch (Exception ex) { _logger.LogDebug(ex, "CodeGraphSyncService: Debounced reindex failed"); }
    }
}
