using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.AI.Governors;

public sealed record ElasticWorkerConfig
{
    public int MinWorkers { get; init; } = 2;
    public int MaxWorkers { get; init; } = 16;
    public TimeSpan ScaleUpDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ScaleDownDelay { get; init; } = TimeSpan.FromSeconds(30);
    public int ScaleUpThreshold { get; init; } = 3;
    public int ScaleDownThreshold { get; init; } = 1;
    public TimeSpan WorkerIdleTimeout { get; init; } = TimeSpan.FromMinutes(2);
}

public sealed class ElasticWorkerPool
{
    private readonly ElasticWorkerConfig _config;
    private readonly ILogger<ElasticWorkerPool> _logger;
    private readonly Channel<string> _workChannel = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _resultChannel = Channel.CreateUnbounded<string>();
    private readonly List<Task> _workers = new();
    private readonly SemaphoreSlim _workerLock = new(1, 1);
    private readonly ConcurrentDictionary<int, DateTime> _workerLastActive = new();
    private int _activeWorkers;
    private int _pendingWork;
    private Func<string, CancellationToken, Task<string>>? _workFactory;
    private CancellationTokenSource? _scaleCts;

    public ElasticWorkerPool(ElasticWorkerConfig? config = null, ILogger<ElasticWorkerPool>? logger = null)
    {
        _config = config ?? new ElasticWorkerConfig();
        _logger = logger ?? NullLogger<ElasticWorkerPool>.Instance;
    }

    public int ActiveWorkers => _activeWorkers;
    public int PendingWork => _pendingWork;

    public async Task StartAsync(Func<string, CancellationToken, Task<string>> workFactory, CancellationToken ct = default)
    {
        _workFactory = workFactory;
        _scaleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        for (var i = 0; i < _config.MinWorkers; i++)
            await AddWorkerAsync(_scaleCts.Token).ConfigureAwait(false);

        _ = BackgroundScaleLoopAsync(_scaleCts.Token);

        _logger.LogInformation("ElasticWorkerPool started with {Count} min workers (max={Max})",
            _config.MinWorkers, _config.MaxWorkers);
    }

    public async Task StopAsync()
    {
        _scaleCts?.Cancel();
        _workChannel.Writer.Complete();

        var workerTasks = _workers.ToArray();
        try { await Task.WhenAll(workerTasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        _logger.LogInformation("ElasticWorkerPool stopped");
    }

    public async Task<string> EnqueueWorkAsync(string workItem, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _pendingWork);
        await _workChannel.Writer.WriteAsync(workItem, ct).ConfigureAwait(false);
        var result = await _resultChannel.Reader.ReadAsync(ct).ConfigureAwait(false);
        Interlocked.Decrement(ref _pendingWork);
        return result;
    }

    public async Task<IReadOnlyList<string>> EnqueueWorkBatchAsync(IEnumerable<string> workItems, CancellationToken ct = default)
    {
        var allResults = new ConcurrentBag<string>();
        var tasks = workItems.Select(async item =>
        {
            var result = await EnqueueWorkAsync(item, ct).ConfigureAwait(false);
            allResults.Add(result);
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return allResults.ToList();
    }

    private async Task AddWorkerAsync(CancellationToken ct)
    {
        var workerId = Interlocked.Increment(ref _activeWorkers);
        _workerLastActive[workerId] = DateTime.UtcNow;

        var workerTask = Task.Run(async () =>
        {
            await foreach (var item in _workChannel.Reader.ReadAllAsync(ct))
            {
                _workerLastActive[workerId] = DateTime.UtcNow;
                try
                {
                    var result = await _workFactory!(item, ct).ConfigureAwait(false);
                    await _resultChannel.Writer.WriteAsync(result, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Worker {Id} failed on item: {Item}", workerId, item[..Math.Min(item.Length, 100)]);
                    try { await _resultChannel.Writer.WriteAsync($"ERROR: {ex.Message}", ct); }
                    catch { }
                }
            }
        }, ct);

        _workers.Add(workerTask);
    }

    private async Task RemoveWorkerAsync()
    {
        await _workerLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_activeWorkers > _config.MinWorkers)
            {
                Interlocked.Decrement(ref _activeWorkers);
                _logger.LogDebug("Scaled down to {Count} workers", _activeWorkers);
            }
        }
        finally
        {
            _workerLock.Release();
        }
    }

    private async Task BackgroundScaleLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_config.ScaleUpDelay, ct).ConfigureAwait(false);

                var queueLength = _workChannel.Reader.Count + _pendingWork;
                var busyWorkers = _workerLastActive.Values
                    .Count(t => (DateTime.UtcNow - t) < _config.WorkerIdleTimeout);

                if (queueLength >= _config.ScaleUpThreshold && _activeWorkers < _config.MaxWorkers)
                {
                    await AddWorkerAsync(ct).ConfigureAwait(false);
                    _logger.LogInformation("Scaled up to {Count} workers (pending={Pending})",
                        _activeWorkers, queueLength);
                }
                else if (queueLength <= _config.ScaleDownThreshold && _activeWorkers > _config.MinWorkers)
                {
                    var idleCount = _activeWorkers - busyWorkers;
                    if (idleCount > 0)
                    {
                        await RemoveWorkerAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scale loop error");
            }
        }
    }
}
